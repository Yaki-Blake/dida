plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.didalite.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.didalite.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 12
        versionName = "0.2.1"
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

dependencies {
    // 界面用原生 View，零第三方依赖；单测只引 JUnit，不参与 APK
    testImplementation("junit:junit:4.13.2")
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

