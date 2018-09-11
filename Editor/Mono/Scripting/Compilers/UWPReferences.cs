// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

// this file is used by both Editor and AssemblyConverter

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Win32;

namespace UnityEditor.Scripting.Compilers
{
    internal struct UWPExtensionSDK
    {
        public readonly string Name;
        public readonly string Version;
        public readonly string ManifestPath;

        public UWPExtensionSDK(string name, string version, string manifestPath)
        {
            Name = name;
            Version = version;
            ManifestPath = manifestPath;
        }
    }

    internal class UWPSDK
    {
        public readonly Version Version;
        public readonly Version MinVSVersion;

        public UWPSDK(Version version, Version minVSVersion)
        {
            Version = version;
            MinVSVersion = minVSVersion;
        }
    }

    internal class PreviousSDKVersion
    {
        public readonly string Version;
        public readonly bool DefaultFallback;

        public PreviousSDKVersion(string version, bool defaultFallback)
        {
            Version = version;
            DefaultFallback = defaultFallback;
        }
    }

    internal static class UWPReferences
    {
        private sealed class UWPExtension
        {
            public string Name { get; private set; }

            public string[] References { get; private set; }

            public UWPExtension(string manifest, string windowsKitsFolder, string sdkVersion)
            {
                var document = XDocument.Load(manifest);
                var fileListElement = document.Element("FileList");
                if (fileListElement.Attribute("TargetPlatform").Value != "UAP")
                    throw new Exception(string.Format("Invalid extension manifest at \"{0}\".", manifest));
                Name = fileListElement.Attribute("DisplayName").Value;
                var containedApiContractsElement = fileListElement.Element("ContainedApiContracts");
                References = GetReferences(windowsKitsFolder, sdkVersion, containedApiContractsElement);
            }
        }

        public static string[] GetReferences(Version sdkVersion)
        {
            var folder = GetWindowsKit10();
            if (string.IsNullOrEmpty(folder))
                return new string[0];

            var version = SdkVersionToString(sdkVersion);
            var references = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            var windowsWinMd = CombinePaths(folder, "UnionMetadata", version, "Facade", "Windows.winmd");
            if (!File.Exists(windowsWinMd))
                windowsWinMd = CombinePaths(folder, "UnionMetadata", "Facade", "Windows.winmd");

            references.Add(windowsWinMd);

            foreach (var reference in GetPlatform(folder, version))
            {
                references.Add(reference);
            }

            foreach (var extension in GetExtensions(folder, version))
            {
                foreach (var reference in extension.References)
                {
                    references.Add(reference);
                }
            }

            return references.ToArray();
        }

        public static IEnumerable<UWPExtensionSDK> GetExtensionSDKs(Version sdkVersion)
        {
            var windowsKit10Directory = GetWindowsKit10();
            if (string.IsNullOrEmpty(windowsKit10Directory))
                return new UWPExtensionSDK[0];

            return GetExtensionSDKs(windowsKit10Directory, SdkVersionToString(sdkVersion));
        }

        static string SdkVersionToString(Version version)
        {
            var sdkVersion = version.ToString();

            if (version.Minor == -1)
                sdkVersion += ".0";
            if (version.Build == -1)
                sdkVersion += ".0";
            if (version.Revision == -1)
                sdkVersion += ".0";

            return sdkVersion;
        }

        public static IEnumerable<KeyValuePair<UWPSDK, IEnumerable<KeyValuePair<string, PreviousSDKVersion>>>> GetInstalledSDKs()
        {
            var windowsKit10Directory = GetWindowsKit10();
            if (string.IsNullOrEmpty(windowsKit10Directory))
                return Enumerable.Empty<KeyValuePair<UWPSDK, IEnumerable<KeyValuePair<string, PreviousSDKVersion>>>>();

            var platformsUAP = CombinePaths(windowsKit10Directory, "Platforms", "UAP");
            if (!Directory.Exists(platformsUAP))
                return Enumerable.Empty<KeyValuePair<UWPSDK, IEnumerable<KeyValuePair<string, PreviousSDKVersion>>>>();

            var allSDKs = new Dictionary<UWPSDK, IEnumerable<KeyValuePair<string, PreviousSDKVersion>>>();

            var filesUnderPlatformsUAP = Directory.GetFiles(platformsUAP, "*", SearchOption.AllDirectories);
            var allPlatformXmlFiles = filesUnderPlatformsUAP.Where(f => string.Equals("Platform.xml", Path.GetFileName(f), StringComparison.OrdinalIgnoreCase));

            foreach (var platformXmlFile in allPlatformXmlFiles)
            {
                XDocument xDocument;

                try
                {
                    xDocument = XDocument.Load(platformXmlFile);
                }
                catch
                {
                    continue;
                }

                foreach (var platformElement in xDocument.Elements("ApplicationPlatform"))
                {
                    Version version;
                    if (FindVersionInNode(platformElement, out version))
                    {
                        var minVSVersionString = platformElement.Elements("MinimumVisualStudioVersion").Select(e => e.Value).FirstOrDefault();

                        // Get supported previous versionss
                        var previousVersionPath = Path.Combine(Path.GetDirectoryName(platformXmlFile), "PreviousPlatforms.xml");
                        var previousVersions = new SortedList<string, PreviousSDKVersion>();

                        if (File.Exists(previousVersionPath))
                        {
                            try
                            {
                                XNamespace xn = "http://microsoft.com/schemas/Windows/SDK/PreviousPlatforms";
                                var previousPlatformsDocument = XDocument.Load(previousVersionPath);

                                foreach (XElement previousPlatformElement in previousPlatformsDocument.Element(xn + "PreviousPlatforms").Elements(xn + "ApplicationPlatform"))
                                {
                                    var previousVersion = previousPlatformElement.Attribute("version").Value;
                                    bool isDefault;

                                    if (!bool.TryParse(previousPlatformElement.Attribute("IsDefaultFallback").Value, out isDefault))
                                    {
                                        isDefault = false;
                                    }

                                    previousVersions.Add(previousVersion, new PreviousSDKVersion(previousVersion, isDefault));
                                }
                            }
                            catch
                            {
                                // Ignore exception. We'll just use the default below.
                            }
                        }

                        if (previousVersions.Count == 0)
                        {
                            // For previous versions, only support the current version if no PreviousVersions.xml was found.
                            previousVersions.Add(version.ToString(), new PreviousSDKVersion(version.ToString(), true));
                        }

                        previousVersions.Reverse();
                        allSDKs.Add(new UWPSDK(version, TryParseVersion(minVSVersionString)), previousVersions);
                    }
                }
            }

            return allSDKs;
        }

        // No Version.TryParse in .NET 3.5 :(
        private static Version TryParseVersion(string s)
        {
            if (!string.IsNullOrEmpty(s))
            {
                try
                {
                    return new Version(s);
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool FindVersionInNode(XElement node, out Version version)
        {
            for (var attribute = node.FirstAttribute; attribute != null; attribute = attribute.NextAttribute)
            {
                if (string.Equals(attribute.Name.LocalName, "version", StringComparison.OrdinalIgnoreCase))
                {
                    version = TryParseVersion(attribute.Value);
                    if (version != null)
                    {
                        return true;
                    }
                }
            }

            version = null;
            return false;
        }

        private static string[] GetPlatform(string folder, string version)
        {
            var platformXml = CombinePaths(folder, @"Platforms\UAP", version, "Platform.xml");
            if (!File.Exists(platformXml))
                return new string[0];

            var document = XDocument.Load(platformXml);
            var applicationPlatformElement = document.Element("ApplicationPlatform");
            if (applicationPlatformElement.Attribute("name").Value != "UAP")
                throw new Exception(string.Format("Invalid platform manifest at \"{0}\".", platformXml));
            var containedApiContractsElement = applicationPlatformElement.Element("ContainedApiContracts");
            return GetReferences(folder, version, containedApiContractsElement);
        }

        private static string CombinePaths(params string[] paths)
        {
            return UnityEditor.Utils.Paths.Combine(paths);
        }

        private static IEnumerable<UWPExtensionSDK> GetExtensionSDKs(string sdkFolder, string sdkVersion)
        {
            var extensions = new List<UWPExtensionSDK>();
            var extensionsFolder = Path.Combine(sdkFolder, "Extension SDKs");

            if (!Directory.Exists(extensionsFolder))
                return new UWPExtensionSDK[0];

            foreach (var extensionFolder in Directory.GetDirectories(extensionsFolder))
            {
                var manifest = CombinePaths(extensionFolder, sdkVersion, "SDKManifest.xml");
                var extensionName = Path.GetFileName(extensionFolder);

                if (File.Exists(manifest))
                {
                    extensions.Add(new UWPExtensionSDK(extensionName, sdkVersion, manifest));
                    continue;
                }

                if (extensionName == "XboxLive")
                {
                    // Workaround for XboxLive SDK bug: currently, its version is always 1.0. Microsoft said they'll fix it in the future.
                    manifest = CombinePaths(extensionFolder, "1.0", "SDKManifest.xml");

                    if (File.Exists(manifest))
                    {
                        extensions.Add(new UWPExtensionSDK(extensionName, "1.0", manifest));
                        continue;
                    }
                }
            }

            return extensions;
        }

        private static UWPExtension[] GetExtensions(string windowsKitsFolder, string version)
        {
            var extensions = new List<UWPExtension>();

            foreach (var extensionSDK in GetExtensionSDKs(windowsKitsFolder, version))
            {
                try
                {
                    var extension = new UWPExtension(extensionSDK.ManifestPath, windowsKitsFolder, version);
                    extensions.Add(extension);
                }
                catch
                {
                    // ignore exceptions
                }
            }

            return extensions.ToArray();
        }

        private static string[] GetReferences(string windowsKitsFolder, string sdkVersion, XElement containedApiContractsElement)
        {
            var references = new List<string>();
            foreach (var apiContractElement in containedApiContractsElement.Elements("ApiContract"))
            {
                var name = apiContractElement.Attribute("name").Value;
                var version = apiContractElement.Attribute("version").Value;
                var reference = CombinePaths(windowsKitsFolder, "References", sdkVersion, name, version, name + ".winmd");
                if (!File.Exists(reference))
                {
                    reference = CombinePaths(windowsKitsFolder, "References", name, version, name + ".winmd");

                    if (!File.Exists(reference))
                        continue;
                }
                references.Add(reference);
            }
            return references.ToArray();
        }

        private static string GetWindowsKit10()
        {
            var programFilesX86 =
                Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            ;
            var folder = Path.Combine(programFilesX86, @"Windows Kits\10\");
            try
            {
                const string keyName = @"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0";
                folder = UnityEditorInternal.RegistryUtil.GetRegistryStringValue(keyName, "InstallationFolder", folder, UnityEditorInternal.RegistryView._32);
            }
            catch
            {
                // ignore exceptions
            }

            if (!Directory.Exists(folder))
                return string.Empty;

            return folder;
        }
    }
}
