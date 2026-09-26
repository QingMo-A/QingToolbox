import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

// Name every APK after the moment it was built, so an older package on a phone
// can never be mistaken for a newer one. The stamp is captured once, when this
// script is evaluated, so all variants of a single build agree.
val buildStamp: String = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US)
    .apply { timeZone = TimeZone.getDefault() }
    .format(Date())

android {
    namespace = "com.qingtoolbox.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.qingtoolbox.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0-alpha"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables {
            useSupportLibrary = true
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

// After each variant assembles, drop a timestamped copy of its APK next to the
// original. Both the debug and release builds are handled, so
// `assembleDebug` yields `build-<stamp>.apk` and `assembleRelease` yields
// `build-<stamp>-release.apk`.
androidComponents {
    onVariants { variant ->
        val variantName = variant.name.replaceFirstChar { it.uppercase() }
        val suffix = if (variant.buildType == "release") "-release" else ""
        val outputDir = layout.buildDirectory.dir("outputs/apk/${variant.buildType}")

        tasks.matching { it.name == "assemble$variantName" }.configureEach {
            doLast {
                val dir = outputDir.get().asFile
                if (!dir.isDirectory) return@doLast
                // Unsigned release builds land as `app-<type>-unsigned.apk`, so
                // accept either spelling instead of hard-coding one.
                val source = dir.listFiles()
                    ?.filter { it.extension == "apk" && !it.name.startsWith("build-") }
                    ?.maxByOrNull { it.lastModified() }
                if (source != null) {
                    val target = dir.resolve("build-$buildStamp$suffix.apk")
                    source.copyTo(target, overwrite = true)
                    logger.lifecycle("Timestamped package: ${target.absolutePath}")
                }
            }
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.androidx.documentfile)
    implementation(libs.androidx.activity.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.zxing.core)

    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
}
