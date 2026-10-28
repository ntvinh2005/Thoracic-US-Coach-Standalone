using System.IO;
using UnityEditor;
using UnityEngine;
// NOTE: not `using UnityEditor.PackageManager` — that namespace and
// UnityEditor itself both expose a `PackageInfo` type, which causes
// CS0104 ambiguity at the call site below. We fully qualify the one
// we want (UnityEditor.PackageManager.PackageInfo) inline instead.

/*
================================================================================
 RoomQRTemplateInstaller
================================================================================

 Auto-installs (and updates) the RoomQR WebGL template from this package's
 own WebGLTemplates/ folder into the consuming project's Assets/WebGLTemplates/
 on every Editor load.

 Why this exists
 ---------------
 Unity's support for WebGL templates inside UPM packages has been inconsistent
 across versions — some Unity releases pick them up in the WebGL Template
 dropdown, others don't. Copying the template into the consuming project's
 Assets/WebGLTemplates/ on import makes it appear as "PROJECT:RoomQR", which
 every Unity version that supports WebGL templates at all reliably resolves.

 How it works
 ------------
   1. On every Editor load (InitializeOnLoad), find this package's actual
      filesystem path via PackageInfo.FindForAssembly. Important: when the
      package is installed via git URL, its real path is under
      Library/PackageCache/com.cssalt.phone-gyro@<hash>/, not Packages/.
      The Packages/ path is a virtual AssetDatabase mount and doesn't exist
      on disk for System.IO calls.
   2. Read Package/package.json's "version" field.
   3. Compare against Assets/WebGLTemplates/RoomQR/.phone-gyro-version (the
      stamp file written on the previous install).
   4. If missing or mismatched, delete the old Assets/ copy and re-copy
      from the package's resolvedPath, skipping .meta files. Write the new
      version into the stamp.
   5. AssetDatabase.Refresh() so Unity picks up the new template.

 Customizing the template per-project
 ------------------------------------
 The installer always overwrites Assets/WebGLTemplates/RoomQR/ on a version
 bump. Any local edits to that folder will be lost. If you need a project-
 specific customization:
   - Rename Assets/WebGLTemplates/RoomQR/ to a different name (e.g.
     RoomQR_Custom). The installer only touches the exact name "RoomQR",
     so your renamed copy is safe.
   - Or fork the package and edit its WebGLTemplates/RoomQR/ at the source.
================================================================================
*/

[InitializeOnLoad]
internal static class RoomQRTemplateInstaller
{
    const string LogPrefix = "[PhoneGyro:TemplateInstaller]";
    const string TemplateName = "RoomQR";
    const string DestRoot = "Assets/WebGLTemplates";
    const string VersionStampFileName = ".phone-gyro-version";

    static RoomQRTemplateInstaller()
    {
        // delayCall defers until the AssetDatabase is fully initialized.
        // Without this, AssetDatabase.Refresh() can fire before Unity is
        // ready and the refresh is silently ignored.
        EditorApplication.delayCall += () => Install(false);
    }

    // Manual escape hatch. Editing only the template (or a package script that
    // Unity didn't recompile) won't trigger the auto-installer; this forces a
    // re-copy on demand and always logs the result.
    [MenuItem("Tools/Phone Gyro/Reinstall WebGL Template")]
    static void ForceReinstallMenu()
    {
        Install(true);
    }

    // internal so PhoneGyroCIBuild can force a synchronous install before a headless
    // build. Returns false when the install could not run at all (package or template
    // source not resolvable) so a CI build refuses to ship the committed copy.
    internal static bool Install(bool force)
    {
        // Resolve this package's real filesystem path via the assembly we
        // live in. Works whether the package is fetched from git (sitting in
        // Library/PackageCache), embedded (Packages/<id>/), or local file://
        // referenced — Unity returns the actual on-disk location either way.
        var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RoomQRTemplateInstaller).Assembly);
        if (packageInfo == null)
        {
            Debug.LogWarning($"{LogPrefix} Could not resolve own package via PackageInfo.FindForAssembly. WebGL template auto-install skipped.");
            return false;
        }

        string packageRoot = packageInfo.resolvedPath;
        string sourcePath = Path.Combine(packageRoot, "WebGLTemplates", TemplateName);
        if (!Directory.Exists(sourcePath))
        {
            Debug.LogWarning($"{LogPrefix} WebGL template source missing at: {sourcePath}");
            return false;
        }

        string packageVersion = packageInfo.version ?? "unknown";
        string destPath = Path.Combine(DestRoot, TemplateName);
        string stampPath = Path.Combine(destPath, VersionStampFileName);

        // Decide whether a (re-)install is needed. Two regimes:
        //
        //  - Immutable installs (git URL / registry): the package contents only
        //    ever change when the version changes, so gate on the version stamp.
        //    Cheap — no per-load file diffing.
        //
        //  - Mutable installs (local `file:` / embedded under Packages/): the
        //    version is STATIC while you iterate on the package, so a version
        //    gate would never re-copy your template edits. Gate on CONTENT
        //    instead — re-copy whenever the source template actually differs
        //    from what's installed. This is what makes local package testing
        //    work without bumping the version every time.
        var src = packageInfo.source;
        bool mutable = src == UnityEditor.PackageManager.PackageSource.Local
                    || src == UnityEditor.PackageManager.PackageSource.Embedded;

        bool upToDate = mutable
            ? !TemplatesDiffer(sourcePath, destPath)
            : (File.Exists(stampPath) && File.ReadAllText(stampPath).Trim() == packageVersion);

        if (upToDate && !force)
        {
            return true;
        }
        if (upToDate && force)
        {
            Debug.Log($"{LogPrefix} Template already matches the package; forcing a re-copy anyway (manual reinstall).");
        }

        // (Re-)install. Delete the old copy and its .meta so Unity generates
        // a fresh folder import. The .meta file lives at <path>.meta, not
        // inside the folder.
        bool updating = Directory.Exists(destPath);
        if (updating)
        {
            string why = mutable ? "(local package content changed)" : $"to package v{packageVersion}";
            Debug.Log($"{LogPrefix} Re-installing WebGL template '{TemplateName}' {why}. Local edits to Assets/WebGLTemplates/{TemplateName}/ are overwritten.");
            Directory.Delete(destPath, recursive: true);
            string destMeta = destPath + ".meta";
            if (File.Exists(destMeta)) File.Delete(destMeta);
        }

        Directory.CreateDirectory(DestRoot);
        CopyDirectoryExcludingMeta(sourcePath, destPath);
        File.WriteAllText(stampPath, packageVersion);

        AssetDatabase.Refresh();

        if (updating)
        {
            Debug.Log($"{LogPrefix} WebGL template '{TemplateName}' updated to v{packageVersion}.");
        }
        else
        {
            Debug.Log($"{LogPrefix} Installed WebGL template '{TemplateName}' from package v{packageVersion}. Select PROJECT:{TemplateName} in Project Settings → Player → WebGL → Resolution and Presentation → WebGL Template.");
        }
        return true;
    }

    // Returns true if the package's template (source) differs from what's
    // installed in Assets (dest): any non-.meta source file missing on the dest
    // side, or present with different bytes. Used only for mutable (local /
    // embedded) packages, where the version stays static during iteration so we
    // can't gate on it. Ignores .meta files (Unity generates those in the dest)
    // and the version stamp (dest-only).
    static bool TemplatesDiffer(string source, string dest)
    {
        if (!Directory.Exists(dest)) return true;
        foreach (string srcFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (srcFile.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
            string rel = srcFile.Substring(source.Length).TrimStart('/', '\\');
            string dstFile = Path.Combine(dest, rel);
            if (!File.Exists(dstFile)) return true;
            if (!FilesEqual(srcFile, dstFile)) return true;
        }
        return false;
    }

    static bool FilesEqual(string a, string b)
    {
        byte[] ba = File.ReadAllBytes(a);
        byte[] bb = File.ReadAllBytes(b);
        if (ba.Length != bb.Length) return false;
        for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
        return true;
    }

    // Copies a directory tree, omitting any .meta files. Unity regenerates
    // .meta files (with new, stable GUIDs) for the destination on next
    // AssetDatabase.Refresh(). Skipping .meta copies avoids any chance of
    // GUID conflicts between the immutable package copy and the live Assets/
    // copy, and means consuming projects don't need to track the package's
    // internal GUIDs.
    static void CopyDirectoryExcludingMeta(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectoryExcludingMeta(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
