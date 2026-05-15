using System.Xml;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class PackagedManifestParserTests
{
    private const string TerminalManifest = @"<?xml version='1.0' encoding='utf-8'?>
<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'
         xmlns:desktop4='http://schemas.microsoft.com/appx/manifest/desktop/windows10/4'
         xmlns:desktop5='http://schemas.microsoft.com/appx/manifest/desktop/windows10/5'>
  <Applications>
    <Application Id='App'>
      <Extensions>
        <desktop4:Extension Category='windows.fileExplorerContextMenus'>
          <desktop4:FileExplorerContextMenus>
            <desktop5:ItemType Type='Directory'>
              <desktop5:Verb Id='OpenTerminalHere' Clsid='9f156763-7844-4dc4-b2b1-901f640f5155' />
            </desktop5:ItemType>
            <desktop5:ItemType Type='Directory\Background'>
              <desktop5:Verb Id='OpenTerminalHere' Clsid='9f156763-7844-4dc4-b2b1-901f640f5155' />
            </desktop5:ItemType>
          </desktop4:FileExplorerContextMenus>
        </desktop4:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>";

    [Fact]
    public void Parse_extracts_itemtypes_and_aumid_for_terminal()
    {
        var doc = new XmlDocument();
        doc.LoadXml(TerminalManifest);
        var info = PackagedShellExtScanner.ParseManifestExtensionInfo(
            doc, "Microsoft.WindowsTerminal_1.24.10921.0_x64__8wekyb3d8bbwe");

        Assert.True(info.ItemTypesByClsid.TryGetValue("{9F156763-7844-4DC4-B2B1-901F640F5155}", out var types));
        Assert.Contains("Directory", types);
        Assert.Contains("Directory\\Background", types);
        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", info.Aumid);
    }

    [Fact]
    public void DerivePackageFamilyName_keeps_name_and_publisher_hash()
    {
        Assert.Equal(
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            PackagedShellExtScanner.DerivePackageFamilyName(
                "Microsoft.WindowsTerminal_1.24.10921.0_x64__8wekyb3d8bbwe"));
    }

    [Fact]
    public void DerivePackageFamilyName_handles_amd_style_double_underscore()
    {
        // The "ResourceId" segment between arch and publisher hash is often empty,
        // producing a double underscore. The publisher hash must still come out
        // unscathed.
        var familyName = PackagedShellExtScanner.DerivePackageFamilyName(
            "AdvancedMicroDevicesInc-2.AMDRadeonSoftware_10.25.10107.0_x64__0a9344xs7nr4m");

        Assert.Equal("AdvancedMicroDevicesInc-2.AMDRadeonSoftware_0a9344xs7nr4m", familyName);
    }

    [Fact]
    public void DerivePackageFamilyName_returns_null_for_unsplittable_input()
    {
        Assert.Null(PackagedShellExtScanner.DerivePackageFamilyName(""));
        Assert.Null(PackagedShellExtScanner.DerivePackageFamilyName("NoUnderscoresHere"));
    }

    [Fact]
    public void Parse_normalises_clsids_to_braced_uppercase()
    {
        // Manifest CLSIDs in real packages are sometimes bare (no braces) and
        // lowercase. The parser must normalise so PackagedShellExt lookups (which
        // come from the registry SurrogateAppId — already braced uppercase) match.
        var manifestNoBraces = @"<?xml version='1.0'?>
<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'>
  <Applications>
    <Application Id='App'>
      <Extensions>
        <desktop4:Extension Category='windows.fileExplorerContextMenus'
                            xmlns:desktop4='http://schemas.microsoft.com/appx/manifest/desktop/windows10/4'>
          <desktop4:FileExplorerContextMenus xmlns:desktop5='http://schemas.microsoft.com/appx/manifest/desktop/windows10/5'>
            <desktop5:ItemType Type='Directory\Background'>
              <desktop5:Verb Id='X' Clsid='aabbccdd-eeff-1122-3344-556677889900' />
            </desktop5:ItemType>
          </desktop4:FileExplorerContextMenus>
        </desktop4:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>";
        var doc = new XmlDocument();
        doc.LoadXml(manifestNoBraces);
        var info = PackagedShellExtScanner.ParseManifestExtensionInfo(doc, "Pkg_1_x64__hash");

        Assert.Contains("{AABBCCDD-EEFF-1122-3344-556677889900}", info.ItemTypesByClsid.Keys);
    }
}
