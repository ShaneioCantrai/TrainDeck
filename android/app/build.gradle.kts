plugins {
    id("com.android.application")
}

android {
    namespace = "ca.maplevibe.traindeck"
    compileSdk = 36

    defaultConfig {
        applicationId = "ca.maplevibe.traindeck"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

