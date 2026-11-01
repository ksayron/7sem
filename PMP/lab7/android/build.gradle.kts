allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

val newBuildDir: Directory =
    rootProject.layout.buildDirectory
        .dir("../../build")
        .get()
rootProject.layout.buildDirectory.value(newBuildDir)

subprojects {
    val newSubprojectBuildDir: Directory = newBuildDir.dir(project.name)
    project.layout.buildDirectory.value(newSubprojectBuildDir)
    if (name == "integration_test") {
        // Align Flutter's test plugin with the app's AGP instead of resolving
        // a second toolchain. The shared Flutter SDK/plugin remains unchanged.
        buildscript.configurations.configureEach {
            resolutionStrategy.eachDependency {
                if (requested.group == "com.android.tools.build" && requested.name == "gradle" && requested.version == "8.11.0") {
                    useVersion("9.1.0")
                }
                if (requested.group == "org.jetbrains.kotlin" && requested.name.startsWith("kotlin-gradle-plugin")) {
                    useVersion("2.4.0")
                }
            }
        }
    }
}
subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}





