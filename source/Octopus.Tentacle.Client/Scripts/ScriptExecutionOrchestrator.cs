﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.ServiceModel;
using Octopus.Tentacle.Client.Capabilities;
using Octopus.Tentacle.Client.ClientServices;
using Octopus.Tentacle.Client.Retries;
using Octopus.Tentacle.Contracts;
using Octopus.Tentacle.Contracts.ScriptServiceV2;
using ILog = Octopus.Diagnostics.ILog;

namespace Octopus.Tentacle.Client.Scripts
{
    internal class ScriptExecutionOrchestrator : IDisposable
    {
        readonly IScriptObserverBackoffStrategy scriptObserverBackOffStrategy;
        readonly RpcCallRetryHandler rpcCallRetryHandler;
        readonly StartScriptCommandV2 startScriptCommand;
        readonly Action<ScriptStatusResponseV2> onScriptStatusResponseReceived;
        readonly Func<CancellationToken, Task> onScriptCompleted;
        readonly TimeSpan onCancellationAbandonCompleteScriptAfter;
        readonly ILog logger;
        readonly IClientScriptService scriptServiceV1;
        readonly IClientScriptServiceV2 scriptServiceV2;
        readonly IClientCapabilitiesServiceV2 capabilitiesServiceV2;

        public ScriptExecutionOrchestrator(IClientScriptService scriptServiceV1,
            IClientScriptServiceV2 scriptServiceV2,
            IClientCapabilitiesServiceV2 capabilitiesServiceV2,
            IScriptObserverBackoffStrategy scriptObserverBackOffStrategy,
            RpcCallRetryHandler rpcCallRetryHandler,
            StartScriptCommandV2 startScriptCommand,
            Action<ScriptStatusResponseV2> onScriptStatusResponseReceived,
            Func<CancellationToken, Task> onScriptCompleted,
            TimeSpan onCancellationAbandonCompleteScriptAfter,
            ILog logger)
        {
            this.scriptServiceV1 = scriptServiceV1;
            this.scriptServiceV2 = scriptServiceV2;
            this.capabilitiesServiceV2 = capabilitiesServiceV2;
            this.scriptObserverBackOffStrategy = scriptObserverBackOffStrategy;
            this.rpcCallRetryHandler = rpcCallRetryHandler;
            this.startScriptCommand = startScriptCommand;
            this.onScriptStatusResponseReceived = onScriptStatusResponseReceived;
            this.onScriptCompleted = onScriptCompleted;
            this.onCancellationAbandonCompleteScriptAfter = onCancellationAbandonCompleteScriptAfter;
            this.logger = logger;
        }

        public async Task<ScriptStatusResponseV2> ExecuteScript(CancellationToken scriptExecutionCancellationToken)
        {
            ScriptStatusResponseV2 scriptStatusResponse;
            ScriptServiceVersion scriptServiceVersionToUse;
            var startScriptCallCount = 0;

            try
            {
                scriptServiceVersionToUse = await DetermineScriptServiceVersionToUse(logger, scriptExecutionCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scriptExecutionCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Script execution was cancelled");
            }

            if (scriptServiceVersionToUse == ScriptServiceVersion.Version2)
            {
                try
                {
                    scriptStatusResponse = await rpcCallRetryHandler.ExecuteWithRetries(
                        ct =>
                        {
                            ++startScriptCallCount;
                            return scriptServiceV2.StartScript(startScriptCommand, new HalibutProxyRequestOptions(ct));
                        },
                        logger,
                        scriptExecutionCancellationToken,
                        // If we are cancelling script execution we can abandon a call to start script
                        // If we manage to cancel the start script call we can walk away
                        // If we do abandon the start script call we have to assume the script is running so need
                        // to call CancelScript and CompleteScript
                        abandonActionOnCancellation: true).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException && scriptExecutionCancellationToken.IsCancellationRequested)
                {
                    // If we are not retrying and we managed to cancel execution it means the request was never sent so we can safely walk away from it.
                    if (e is not OperationAbandonedException && startScriptCallCount <= 1)
                    {
                        throw;
                    }

                    // Otherwise we have to assume the script started executing and call CancelScript and CompleteScript
                    // We don't have a response so we need to create one to continue the execution flow
                    scriptStatusResponse = new ScriptStatusResponseV2(
                        startScriptCommand.ScriptTicket,
                        ProcessState.Pending,
                        ScriptExitCodes.RunningExitCode,
                        new List<ProcessOutput>(),
                        0);

                    await ObserveUntilCompleteThenFinish(scriptServiceVersionToUse, scriptStatusResponse, scriptExecutionCancellationToken).ConfigureAwait(false);

                    // Throw an error so the caller knows that execution of the script was cancelled
                    throw new OperationCanceledException("Script execution was cancelled");
                }
            }
            else
            {
                var startScriptCommandV1 = Map(startScriptCommand);
                var scriptTicket = scriptServiceV1.StartScript(startScriptCommandV1, new HalibutProxyRequestOptions(scriptExecutionCancellationToken));

                scriptStatusResponse = Map(scriptTicket);
            }

            var response = await ObserveUntilCompleteThenFinish(scriptServiceVersionToUse, scriptStatusResponse, scriptExecutionCancellationToken).ConfigureAwait(false);

            if (scriptExecutionCancellationToken.IsCancellationRequested)
            {
                // Throw an error so the caller knows that execution of the script was cancelled
                throw new OperationCanceledException("Script execution was cancelled");
            }

            return response;
        }

        async Task<ScriptServiceVersion> DetermineScriptServiceVersionToUse(ILog logger, CancellationToken cancellationToken)
        {
            logger.Verbose("Determining ScriptService version to use");

            var tentacleCapabilities = await rpcCallRetryHandler.ExecuteWithRetries(
                ct =>
                {
                    return capabilitiesServiceV2.GetCapabilities(new HalibutProxyRequestOptions(ct));
                },
                logger,
                cancellationToken,
                // We can abandon a call to Get Capabilities and walk away as this is not running anything that needs to be cancelled on Tentacle
                abandonActionOnCancellation: true).ConfigureAwait(false);

            logger.Verbose($"Discovered Tentacle capabilities: {string.Join(",", tentacleCapabilities.SupportedCapabilities)}");

            if (tentacleCapabilities.HasScriptServiceV2())
            {
                logger.Verbose("Using ScriptServiceV2");
                logger.Verbose($"RPC call retries are enabled. Retry timeout {rpcCallRetryHandler.RetryTimeout.TotalSeconds} seconds");
                return ScriptServiceVersion.Version2;
            }

            logger.Verbose("RPC call retries are enabled but will not be used for Script Execution as a compatible ScriptService was not found. Please upgrade Tentacle to enable this feature.");
            logger.Verbose("Using ScriptServiceV1");
            return ScriptServiceVersion.Version1;
        }

        async Task<ScriptStatusResponseV2> ObserveUntilCompleteThenFinish(
            ScriptServiceVersion scriptServiceVersionToUse,
            ScriptStatusResponseV2 scriptStatusResponse,
            CancellationToken scriptExecutionCancellationToken)
        {
            onScriptStatusResponseReceived(scriptStatusResponse);

            var lastScriptStatus = await ObserveUntilComplete(scriptServiceVersionToUse, scriptStatusResponse, scriptExecutionCancellationToken).ConfigureAwait(false);

            await onScriptCompleted(scriptExecutionCancellationToken);

            lastScriptStatus = await Finish(scriptServiceVersionToUse, lastScriptStatus, scriptExecutionCancellationToken).ConfigureAwait(false);

            return lastScriptStatus;
        }

        async Task<ScriptStatusResponseV2> ObserveUntilComplete(
            ScriptServiceVersion scriptServiceVersionToUse,
            ScriptStatusResponseV2 scriptStatusResponse,
            CancellationToken scriptExecutionCancellationToken)
        {
            var lastStatusResponse = scriptStatusResponse;
            var iteration = 0;
            var cancellationIteration = 0;

            while (lastStatusResponse.State != ProcessState.Complete)
            {
                if (scriptExecutionCancellationToken.IsCancellationRequested)
                {
                    lastStatusResponse = await Cancel(scriptServiceVersionToUse, lastStatusResponse).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        lastStatusResponse = await GetStatus(scriptServiceVersionToUse, lastStatusResponse, scriptExecutionCancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        if(scriptExecutionCancellationToken.IsCancellationRequested) continue; // Enter cancellation mode.
                        throw;
                    }
                }

                onScriptStatusResponseReceived(lastStatusResponse);

                if (lastStatusResponse.State == ProcessState.Complete)
                {
                    continue;
                }

                if (scriptExecutionCancellationToken.IsCancellationRequested)
                {
                    // When cancelling we want to back-off between checks to see if the script has cancelled but restart from iteration 0
                    await Task.Delay(scriptObserverBackOffStrategy.GetBackoff(++cancellationIteration), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(scriptObserverBackOffStrategy.GetBackoff(++iteration), scriptExecutionCancellationToken)
                        .SuppressOperationCanceledException()
                        .ConfigureAwait(false);
                }
            }

            return lastStatusResponse;
        }

        async Task<ScriptStatusResponseV2> GetStatus(
            ScriptServiceVersion scriptServiceVersionToUse,
            ScriptStatusResponseV2 lastStatusResponse,
            CancellationToken cancellationToken)
        {
            if (scriptServiceVersionToUse == ScriptServiceVersion.Version2)
            {
                try
                {
                    return await rpcCallRetryHandler.ExecuteWithRetries(
                    ct =>
                    {
                        return scriptServiceV2.GetStatus(new ScriptStatusRequestV2(lastStatusResponse.Ticket, lastStatusResponse.NextLogSequence), new HalibutProxyRequestOptions(ct));
                    },
                    logger,
                    cancellationToken,
                    // If cancelling script execution we can abandon a call to GetStatus and go straight into the CancelScript and CompleteScript flow
                    abandonActionOnCancellation: true).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    // Return the last known response without logs when cancellation occurs and let the script execution go into the CancelScript and CompleteScript flow
                    return new ScriptStatusResponseV2(lastStatusResponse.Ticket, lastStatusResponse.State, lastStatusResponse.ExitCode, new List<ProcessOutput>(), lastStatusResponse.NextLogSequence);
                }
            }
            else
            {
                var scriptStatusResponseV1 = scriptServiceV1.GetStatus(new ScriptStatusRequest(lastStatusResponse.Ticket, lastStatusResponse.NextLogSequence), new HalibutProxyRequestOptions(cancellationToken));
                return Map(scriptStatusResponseV1);
            }
        }

        async Task<ScriptStatusResponseV2> Cancel(
            ScriptServiceVersion scriptServiceVersionToUse,
            ScriptStatusResponseV2 lastStatusResponse)
        {
            // We don't want to cancel this operation as it is responsible for stopping the script executing on the Tentacle
            var cancellationToken = CancellationToken.None;
            if (scriptServiceVersionToUse == ScriptServiceVersion.Version2)
            {
                return await rpcCallRetryHandler.ExecuteWithRetries(
                    ct =>
                    {
                        return scriptServiceV2.CancelScript(new CancelScriptCommandV2(lastStatusResponse.Ticket, lastStatusResponse.NextLogSequence), new HalibutProxyRequestOptions(ct));
                    },
                    logger,
                    cancellationToken,
                    // We don't want to abandon this operation as it is responsible for stopping the script executing on the Tentacle
                    abandonActionOnCancellation: false).ConfigureAwait(false);
            }
            else
            {
                var scriptStatusResponseV1 = scriptServiceV1.CancelScript(new CancelScriptCommand(lastStatusResponse.Ticket, lastStatusResponse.NextLogSequence), new HalibutProxyRequestOptions(cancellationToken));
                return Map(scriptStatusResponseV1);
            }
        }

        async Task<ScriptStatusResponseV2> Finish(
            ScriptServiceVersion scriptServiceVersionToUse,
            ScriptStatusResponseV2 lastStatusResponse,
            CancellationToken scriptExecutionCancellationToken)
        {
            ScriptStatusResponseV2 completeStatus;

            if (scriptServiceVersionToUse == ScriptServiceVersion.Version2)
            {
                // Best effort cleanup of Tentacle
                try
                {
                    var actionTask = Task.Run(() => scriptServiceV2.CompleteScript(new CompleteScriptCommandV2(lastStatusResponse.Ticket), new HalibutProxyRequestOptions(scriptExecutionCancellationToken)), CancellationToken.None);

                    var abandonCancellationTokenSource = new CancellationTokenSource();

                    void CancelAfter()
                    {
                        // If we are cancelling then try and call CompleteScript for a short period of time
                        abandonCancellationTokenSource.TryCancelAfter(onCancellationAbandonCompleteScriptAfter);
                    }

                    using (scriptExecutionCancellationToken.Register(CancelAfter))
                    {
                        if (scriptExecutionCancellationToken.IsCancellationRequested)
                        {
                            CancelAfter();
                        }

                        var abandonTask = abandonCancellationTokenSource.Token.AsTask<ScriptStatusResponseV2>();
                        await (await Task.WhenAny(actionTask, abandonTask).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is HalibutClientException or OperationCanceledException)
                {
                    logger.Warn("Failed to cleanup the script working directory on Tentacle");
                    logger.Verbose(ex);
                }

                completeStatus = lastStatusResponse;
            }
            else
            {
                var completeStatusV1 = scriptServiceV1.CompleteScript(new CompleteScriptCommand(lastStatusResponse.Ticket, lastStatusResponse.NextLogSequence), new HalibutProxyRequestOptions(CancellationToken.None));
                completeStatus = Map(completeStatusV1);
                onScriptStatusResponseReceived(completeStatus);
            }

            return completeStatus;
        }

        static ScriptStatusResponseV2 Map(ScriptTicket ticket)
        {
            return new ScriptStatusResponseV2(ticket,
                ProcessState.Pending,
                0,
                new List<ProcessOutput>(),
                0);
        }

        static ScriptStatusResponseV2 Map(ScriptStatusResponse scriptStatusResponse)
        {
            return new ScriptStatusResponseV2(scriptStatusResponse.Ticket,
                scriptStatusResponse.State,
                scriptStatusResponse.ExitCode,
                scriptStatusResponse.Logs,
                scriptStatusResponse.NextLogSequence);
        }

        static StartScriptCommand Map(StartScriptCommandV2 command)
        {
            return new StartScriptCommand(
                command.ScriptBody,
                command.Isolation,
                command.ScriptIsolationMutexTimeout,
                command.IsolationMutexName!,
                command.Arguments,
                command.TaskId!,
                command.Scripts,
                command.Files?.ToArray() ?? Array.Empty<ScriptFile>());
        }

        public void Dispose()
        {
        }
    }
}