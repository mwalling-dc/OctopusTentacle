using Halibut.Diagnostics;

namespace Octopus.Tentacle.Tests.Integration.Support
{
    public class HalibutTimeoutsAndLimitsForTestBuilder
    {

        public HalibutTimeoutsAndLimits Build()
        {
            var halibutTimeoutAndLimits = HalibutTimeoutsAndLimits.RecommendedValues();
            return halibutTimeoutAndLimits;
        }
    }
}