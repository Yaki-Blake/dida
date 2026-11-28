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
        versionCode = 14
        versionName = "0.2.3"
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
    // 界面全部用原生 View，不引 androidx；单测只引 JUnit，不参与 APK。
    // 唯一的运行时依赖是 zxing 的 core：纯 Java、无 Android 依赖、无传递依赖，
    // 只用来把扫码拿到的图像解成字符串（第二课堂的「扫码签到签退」）。
    implementation("com.google.zxing:core:3.5.3")
    testImplementation("junit:junit:4.13.2")
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

