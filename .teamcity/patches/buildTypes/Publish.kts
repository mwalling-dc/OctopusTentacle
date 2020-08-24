package patches.buildTypes

import jetbrains.buildServer.configs.kotlin.v2019_2.*
import jetbrains.buildServer.configs.kotlin.v2019_2.ui.*

/*
This patch script was generated by TeamCity on settings change in UI.
To apply the patch, change the buildType with id = 'Publish'
accordingly, and delete the patch script.
*/
changeBuildType(RelativeId("Publish")) {
    dependencies {
        expect(RelativeId("Build")) {
            snapshot {
                onDependencyFailure = FailureAction.CANCEL
                onDependencyCancel = FailureAction.CANCEL
            }

            artifacts {
                cleanDestination = true
                artifactRules = "Octopus.Shared.*.nupkg"
            }
        }
        update(RelativeId("Build")) {
            snapshot {
                onDependencyFailure = FailureAction.FAIL_TO_START
            }

            artifacts {
                cleanDestination = true
                artifactRules = "Octopus.Shared.*.nupkg"
            }
        }

    }
}
