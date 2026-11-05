import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// Версия ТВ-клиента (своя, не как у десктопа); релизы — с тегом tv-v<версия>.
val tvVersion = "0.1.0"

// Ключ подписи релизов — вне репозитория: %USERPROFILE%\.cupoftea\keystore.properties и release.jks
// (scripts\new-release-key.ps1). Потеряете ключ — обновления поверх установленной версии не встанут.
val keystoreProps = Properties().apply {
    val f = File(System.getProperty("user.home"), ".cupoftea/keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "cupoftea.tv"
    // Compose 1.12 требует компиляции под API 37
    compileSdk = 37

    defaultConfig {
        applicationId = "io.github.mdfillin.cupoftea.tv"
        minSdk = 24
        targetSdk = 36
        // 0.1.0 → 100, 1.2.3 → 10203
        versionCode = tvVersion.split('.').map { it.toInt() }.let { (a, b, c) -> a * 10000 + b * 100 + c }
        versionName = tvVersion
    }

    signingConfigs {
        if (keystoreProps.getProperty("storeFile") != null) create("release") {
            storeFile = File(keystoreProps.getProperty("storeFile"))
            storePassword = keystoreProps.getProperty("storePassword")
            keyAlias = keystoreProps.getProperty("keyAlias")
            keyPassword = keystoreProps.getProperty("keyPassword")
        }
    }

    // Свой APK на каждую архитектуру: ядро xray — основная часть размера.
    splits {
        abi {
            isEnable = true
            reset()
            include("arm64-v8a", "armeabi-v7a", "x86_64")
            isUniversalApk = false
        }
    }

    buildTypes {
        // Отладочные сборки — тем же ключом: тогда их можно ставить поверх релизной и наоборот без потери данных.
        debug {
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
        release {
            signingConfig = signingConfigs.findByName("release")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        jniLibs {
            // Ядро в APK сжато: APK ~15 МБ вместо ~40 — обновления качаются втрое быстрее.
            useLegacyPackaging = true
        }
    }
}

kotlin {
    jvmToolchain(21)
}

dependencies {
    implementation(project(":core"))
    // libcup.aar — xray, собирается android\scripts\build-core.ps1
    implementation(fileTree(mapOf("dir" to "libs", "include" to listOf("*.aar"))))
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.foundation)
    implementation(libs.compose.ui)
    implementation(libs.tv.material)
    implementation(libs.activity.compose)
    implementation(libs.coroutines.android)
    // QR-код адреса страницы для телефона (только генерация, без камеры)
    implementation(libs.zxing.core)
}
