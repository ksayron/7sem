pluginManagement {
    // Opt-in only: normal launches keep network dependency resolution enabled.
    if (System.getenv("SPORTS_LAB7_OFFLINE") == "1") {
        gradle.startParameter.isOffline = true
    }
    // Flutter rewrites sdk.dir before invoking Gradle. Keep this project's
    // prepared SDK (including its NDK) when it exists on this workstation.
    // -Pcoffee.androidSdkDir can select another SDK explicitly.
    val preparedSdk = file(providers.gradleProperty("coffee.androidSdkDir").getOrElse("D:/Android/sdk-coffee"))
    if (preparedSdk.isDirectory) {
        val localPropertiesFile = file("local.properties")
        val localProperties = java.util.Properties()
        localPropertiesFile.inputStream().use { localProperties.load(it) }
        val sdkPath = preparedSdk.absolutePath.replace('\\', '/')
        if (localProperties.getProperty("sdk.dir") != sdkPath) {
            localProperties.setProperty("sdk.dir", sdkPath)
            localPropertiesFile.outputStream().use { localProperties.store(it, null) }
        }
        // Flutter's NDK inventory must describe the same SDK as sdk.dir.
        val installedNdks = preparedSdk.resolve("ndk").listFiles()
            ?.filter { it.resolve("source.properties").isFile }
            ?.joinToString(",") { it.name }.orEmpty()
        gradle.beforeProject {
            extensions.extraProperties.set("flutter.androidSdkRoot", sdkPath)
            extensions.extraProperties.set("flutter.installedNdkVersions", installedNdks)
        }
    }

    val flutterSdkPath =
        run {
            val properties = java.util.Properties()
            file("local.properties").inputStream().use { properties.load(it) }
            val flutterSdkPath = properties.getProperty("flutter.sdk")
            require(flutterSdkPath != null) { "flutter.sdk not set in local.properties" }
            flutterSdkPath
        }

    includeBuild("$flutterSdkPath/packages/flutter_tools/gradle")

    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

plugins {
    id("dev.flutter.flutter-plugin-loader") version "1.0.0"
    id("com.android.application") version "9.1.0" apply false
    id("org.jetbrains.kotlin.android") version "2.4.0" apply false
}

include(":app")





