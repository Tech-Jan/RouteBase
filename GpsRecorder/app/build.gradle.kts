plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.ksp)
    alias(libs.plugins.dependency.check)
    alias(libs.plugins.detekt)
}

// Analyzer dragnet, Kotlin half: different rule inventory than Android lint.
// On-demand like lint: ./gradlew :app:detekt — never part of a normal build. ignoreFailures
// while in inventory mode; adopted rule families get fixed to zero, then this flips false.
detekt {
    buildUponDefaultConfig = true
    ignoreFailures = true
}

// On-demand CVE scan (like lint — never part of a normal build): ./gradlew :app:dependencyCheckAnalyze
// Needs network for the NVD DB; set NVD_API_KEY to speed the first download from hours to minutes.
dependencyCheck {
    analyzers.assemblyEnabled = false // .NET analyzer wants dotnet/mono; irrelevant here, warns otherwise
    System.getenv("NVD_API_KEY")?.let { nvd.apiKey = it }
    // Only what ships in the APK. The first unscoped run drowned in compiler-classpath jars
    // (netty/protobuf/sqlite-jdbc via Room/KSP tooling) that never reach the device.
    scanConfigurations = listOf("releaseRuntimeClasspath")
    suppressionFile = rootProject.file("dependency-check-suppressions.xml").absolutePath
}

android {
    namespace = "com.routebase.gpsrecorder"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.routebase.gpsrecorder"
        minSdk = 26
        targetSdk = 36
        versionCode = 24
        versionName = "1.6.8"
    }

    // Release signing comes ONLY from the environment (the release workflow's secrets). Unset → local release
    // builds stay unsigned, and the key never has to exist on a dev machine or in the repo.
    signingConfigs {
        System.getenv("ROUTEBASE_KEYSTORE")?.let { keystore ->
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("ROUTEBASE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ROUTEBASE_KEY_ALIAS")
                keyPassword = System.getenv("ROUTEBASE_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release")
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    buildFeatures {
        viewBinding = true
        buildConfig = true
    }

    testOptions {
        // The upload path (PointDelivery) logs via android.util.Log, which is a stub in JVM unit tests and
        // throws "not mocked" by default — that alone would make its invariants untestable without
        // Robolectric. Default-values instead: Log.w returns 0, EventLog no-ops (its file is never init'd).
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.material)
    implementation(libs.androidx.constraintlayout)
    implementation(libs.play.services.location)
    implementation(libs.retrofit)
    implementation(libs.retrofit.converter.gson)
    implementation(libs.okhttp)
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.lifecycle.viewmodel.ktx)
    implementation(libs.lifecycle.runtime.ktx)
    implementation(libs.work.runtime.ktx)
    testImplementation(libs.junit)
    // runTest's virtual clock: PointDelivery paces batches 1.1 s apart and backs off 1.5 s on a 429, so a
    // real-time drain test would sleep for tens of seconds.
    testImplementation(libs.kotlinx.coroutines.test)
}
