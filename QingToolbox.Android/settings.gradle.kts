import org.gradle.api.initialization.resolve.RepositoriesMode

pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        google()
        // Keep the pinned Noise artifact buildable where Maven Central's TLS
        // endpoint is unavailable. The mirror is scoped to this one group.
        maven("https://maven.aliyun.com/repository/public") {
            content { includeGroup("com.github.auties00") }
        }
        mavenCentral()
    }
}

rootProject.name = "QingToolbox.Android"
include(":app")
