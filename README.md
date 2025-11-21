# Bluetooth Custom Transport for Unity Netcode for GameObjects

This repository provides a custom communication layer for **Netcode for GameObjects (NGO)** using **Bluetooth** as its underlying transport mechanism. The package is designed for Android and requires a **minimum target API level of 33**.

The transport supports a stable two-device connection using predefined roles (**Server** and **Client**). If the client crashes and restarts the application, it can seamlessly reconnect to the server without requiring the host to reconfigure its state.

---

## 📦 Project Structure

Below are the key folders and files included in this package:

- [`Plugin`](Plugin): Contains the custom Bluetooth Android plugin and configuration templates
  - [`BlueT-release.aar`](Plugin/BlueT-release.aar): Precompiled Android plugin
  - [`Manifest_and_gradle_configs.txt`](Plugin/Manifest_and_gradle_configs.txt): Required lines for Android manifest and Gradle templates
  - `SourceCode_plugin.java`: Source code for rebuilding the .aar plugin
  - `build.gradle.kts`: Reference Gradle configuration for plugin reconstruction
- [`Scripts`](Scripts): C# scripts for integrating the Bluetooth transport into your Unity project, including an example of partial implementation [`BTforNetcodeExample.cs`](Scripts/BTforNetcodeExample.cs)

---

# 1. Unity Project Setup

To correctly integrate the Bluetooth transport plugin into your Unity project, follow the steps below.

## 1.1 Enable Custom Android Build Templates

In Unity, navigate to:

**Project Settings → Player → Publishing Settings**

Enable the following options:
- **Custom Main Manifest**
- **Custom Main Gradle Template**
- **Custom Gradle Properties Template**

These will allow modification of Unity's default Android build configuration.

## 1.2 Install the Bluetooth Plugin

1. Copy the file [`BlueT-release.aar`](Plugin/BlueT-release.aar) into your Unity project directory:
   
   ```
   Assets/Plugins/Android
   ```

2. Select the `.aar` file inside Unity. In the **Inspector**, enable:
   
   - **Load on Startup** → `true`

   Then click **Apply**.

## 1.3 Update Manifest and Gradle Templates

In the folder:

```
Assets/Plugins/Android
```

modify the following files:
- `AndroidManifest.xml`
- `mainTemplate.gradle`
- `gradleTemplate.properties`

Add all required lines found in:

- [`Manifest_and_gradle_configs.txt`](Plugin/Manifest_and_gradle_configs.txt)

These entries are required for Bluetooth permissions, features, and Gradle compatibility with the plugin.

## 1.4 Set API Levels

In Unity:

**Project Settings → Player → Other Settings**

Ensure:
- **Target API Level**: **33** (mandatory, you can try to rebuild the .aar the only constrain was the trivial use of the library: androidx.annotation.RequiresPermission)
- (the original project used a Minimum API Level = 29)

## 1.5 Import C# Scripts

Import all scripts inside:

- [`Scripts`](Scripts)

Place them in any folder **inside Assets**, except **Plugins** or **Editor**.



---

# 2. Rebuilding the Android Plugin (Optional)
If you need to modify or extend the Bluetooth Android plugin, follow the official reconstruction procedure below.

## 2.1 Create a New Android Studio Project

1. Open **Android Studio** and create a new project.
2. Choose **Gradle Kotlin DSL (gradle.kts)** as your configuration (recommended).
3. Create a **new module**, then delete all unnecessary files so that the module is recognized as an **Android Library**.

In `settings.gradle`, ensure:
```
include(":<YourModuleName>")
```
Remove:
```
include(":app")
```

## 2.2 Add the Plugin Source Code

1. Rename:
   
   `SourceCode_plugin.java` → `BluetoothPlugin.java`

2. Place the file inside your module’s Java source path:

```
<ModuleDirectory>/src/main/java/com/<YourProjectName>
```

## 2.3 Configure Gradle

Inside the module directory, locate:

```
<ModuleDirectory>/build.gradle.kts
```

Overwrite its contents with the provided reference configuration:

- [`build.gradle.kts`](Plugin/build.gradle.kts)

Sync the project to follow the new gradle rules.

## 2.4 Build the Plugin

Open the Gradle console and run:

```
./gradlew :<YourModuleName>:assembleRelease
```

After the build completes, the generated `.aar` file will be located in:

```
<ModuleDirectory>/build/outputs/aar
```

Use this file to replace `BlueT-release.aar` in your Unity project.

---
 