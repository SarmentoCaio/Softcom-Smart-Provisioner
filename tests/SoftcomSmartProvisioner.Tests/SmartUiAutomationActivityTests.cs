using System.Reflection;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartUiAutomationActivityTests
{
    [Fact]
    public void StandardProvisioningAcceptsInstalledRedeflexPackageOnK2()
    {
        var selected = SelectProvisioningPackage(
            "softcom.mobile.smart2.redeflex",
            configuredPackageInstalled: true,
            new SmartPackageDetection(
                "softcom.mobile.smart2.redeflex",
                "8.1.0",
                new[] { "softcom.mobile.smart2.redeflex" }));

        Assert.Equal("softcom.mobile.smart2.redeflex", selected);
    }

    [Fact]
    public void StandardProvisioningUsesDetectedRedeflexPackageWhenItIsTheOnlySmart()
    {
        var selected = SelectProvisioningPackage(
            configuredPackage: null,
            configuredPackageInstalled: false,
            new SmartPackageDetection(
                "softcom.mobile.smart2.redeflex",
                "8.1.0",
                new[] { "softcom.mobile.smart2.redeflex" }));

        Assert.Equal("softcom.mobile.smart2.redeflex", selected);
    }

    [Theory]
    [InlineData("Smart 8.1+", 25, "smart_totem", "totemk2", "softcom.mobile.smart2.redeflex", true, true)]
    [InlineData("Smart 8.1+", 25, "smart_autopagamento", "default", "softcom.mobile.smart2.redeflex", true, true)]
    [InlineData("Smart legado (< 8.1)", 25, "smart_totem", "totemk2", "softcom.mobile.smart2.redeflex", true, false)]
    [InlineData("Smart 8.1+", 30, "smart_totem", "totemk2", "softcom.mobile.smart2.redeflex", true, false)]
    [InlineData("Smart 8.1+", 25, "smart_pdv", "totemk2", "softcom.mobile.smart2.redeflex", true, false)]
    [InlineData("Smart 8.1+", 25, "smart_totem", "totemk2", "softcom.mobile.smart2.redeflex", false, false)]
    public void K2Smart81OnboardingIsSeparatedFromLegacyAndModernFlows(
        string smartFlow,
        int sdkLevel,
        string module,
        string profile,
        string packageName,
        bool clearData,
        bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ShouldUseSmart81K2OnboardingFlow",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ShouldUseSmart81K2OnboardingFlow");

        Assert.Equal(
            expected,
            (bool)(method.Invoke(
                null,
                new object?[] { smartFlow, sdkLevel, module, profile, packageName, clearData }) ?? false));
    }

    [Theory]
    [InlineData("smart_pdv", 239)]
    [InlineData("smart_comanda", 335)]
    [InlineData("smart_pre_venda", 431)]
    [InlineData("smart_tef", 527)]
    [InlineData("smart_minimercado", 623)]
    [InlineData("smart_totem", 719)]
    [InlineData("smart_autopagamento", 815)]
    [InlineData("desconhecido", 0)]
    public void K2Smart81UsesItsOwnModuleLayout(string module, int expectedReferenceY)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "GetSmart81K2ModuleReferenceY",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "GetSmart81K2ModuleReferenceY");

        Assert.Equal(expectedReferenceY, (int)(method.Invoke(null, new object?[] { module }) ?? -1));
    }

    [Theory]
    [InlineData("softcom.mobile.smart2/softcom.mobile.smart.views.activities.login.LoginActivity", true)]
    [InlineData("softcom.mobile.smart/softcom.mobile.smart.views.activities.login.LoginActivity", true)]
    [InlineData("com.mercadopago.smartpos/com.mercadopago.devtools.login.view.ui.TestUserLoginActivity", false)]
    [InlineData("com.smartpos.launcher/.MainActivity", false)]
    public void OnlySoftcomSmartPackageIsAcceptedAsLegacySmartActivity(string activity, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsLegacySmartPackageActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "IsLegacySmartPackageActivity");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object[] { activity }) ?? false));
    }

    [Fact]
    public void ModuleSelectionIsRecognizedAsDifferentFromDeviceConfiguration()
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsLegacyCompanyAddConfigActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsLegacyCompanyAddConfigActivity");

        const string activity =
            "softcom.mobile.smart2/softcom.mobile.smart.views.activities.EmpresaAddConfigActivity";

        Assert.True((bool)(method.Invoke(null, new object[] { activity }) ?? false));
    }

    [Theory]
    [InlineData("softcom.mobile.smart2.redeflex/softcom.mobile.smart.tef.ui.TefSetupActivity", true)]
    [InlineData("softcom.mobile.smart2/softcom.mobile.smart.tef.ui.TefSetupActivity", true)]
    [InlineData("softcom.mobile.smart2/softcom.mobile.smart.views.activities.EmpresaAddConfigActivity", false)]
    [InlineData("com.mercadopago.smartpos/com.mercadopago.devtools.login.view.ui.TestUserLoginActivity", false)]
    public void SmartTefSetupActivityIsRecognizedWithoutAcceptingAnotherPackage(string activity, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsSmartTefSetupActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsSmartTefSetupActivity");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object[] { activity }) ?? false));
    }

    [Theory]
    [InlineData("softcom.mobile.smart2.redeflex/softcom.mobile.smart.tef.ui.TefActivity", true)]
    [InlineData("softcom.mobile.smart2.redeflex/softcom.mobile.smart.tef.ui.TefSetupActivity", false)]
    [InlineData("com.mercadopago.smartpos/com.mercadopago.devtools.login.view.ui.TestUserLoginActivity", false)]
    public void SmartTefOperationalActivityIsRecognized(string activity, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsSmartTefMainActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsSmartTefMainActivity");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object[] { activity }) ?? false));
    }

    [Theory]
    [InlineData("softcom.mobile.smart2", true)]
    [InlineData("softcom.mobile.smart2.redeflex", true)]
    [InlineData("softcom.mobile.smart", true)]
    [InlineData("com.mercadopago.smartpos", false)]
    [InlineData("com.redeflex.smart", false)]
    [InlineData(null, false)]
    public void ActivityBasedTefNavigationDependsOnOfficialSmartPackageNotDeviceProfile(
        string? packageName,
        bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsActivityBasedSmartTefPackage",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsActivityBasedSmartTefPackage");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { packageName }) ?? false));
    }

    [Theory]
    [InlineData("softcom.mobile.smart2", "Smart 8.1+", false)]
    [InlineData("softcom.mobile.smart2.redeflex", "Smart 8.1+", false)]
    [InlineData("softcom.mobile.smart2", "Smart legado (< 8.1)", true)]
    [InlineData("com.mercadopago.smartpos", "Smart legado (< 8.1)", false)]
    public void SmartTefNavigationUsesVersionInsteadOfAssumingRouteFromPackage(
        string packageName,
        string smartFlow,
        bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ShouldUseActivityBasedSmartTefNavigation",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ShouldUseActivityBasedSmartTefNavigation");

        Assert.Equal(
            expected,
            (bool)(method.Invoke(null, new object?[] { packageName, smartFlow }) ?? false));
    }

    [Theory]
    [InlineData(
        "softcom.mobile.smart2/softcom.mobile.smart.views.activities.loginnew.AuthActivity",
        "softcom.mobile.smart2",
        true)]
    [InlineData(
        "softcom.mobile.smart2.redeflex/softcom.mobile.smart.tef.ui.TefSetupActivity",
        "softcom.mobile.smart2.redeflex",
        true)]
    [InlineData(
        "softcom.mobile.smart2/softcom.mobile.smart.views.activities.loginnew.AuthActivity",
        "softcom.mobile.smart2.redeflex",
        false)]
    public void SmartTefAcceptsAuthActivityOnlyForTheSameSmart81Package(
        string activity,
        string packageName,
        bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsExpectedSmartTefActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsExpectedSmartTefActivity");

        Assert.Equal(
            expected,
            (bool)(method.Invoke(null, new object?[] { activity, packageName }) ?? false));
    }

    private static string SelectProvisioningPackage(
        string? configuredPackage,
        bool configuredPackageInstalled,
        SmartPackageDetection detection)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "SelectProvisioningPackage",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(SmartUiAutomationService).FullName, "SelectProvisioningPackage");

        return (string)(method.Invoke(
            null,
            new object?[] { configuredPackage, configuredPackageInstalled, detection })
            ?? string.Empty);
    }

    [Theory]
    [InlineData("mercadopagon950", true)]
    [InlineData("MERCADOPAGON950", true)]
    [InlineData("stonel400", false)]
    [InlineData(null, false)]
    public void MercadoPagoN950PreservesKeyboardBeforeLongPress(string? profile, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ShouldPreserveLegacy80Keyboard",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ShouldPreserveLegacy80Keyboard");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { profile }) ?? false));
    }

    [Theory]
    [InlineData("smart_autopagamento", true)]
    [InlineData("SMART_AUTOPAGAMENTO", true)]
    [InlineData("smart_totem", true)]
    [InlineData("smart_comanda", false)]
    [InlineData("smart_pdv", false)]
    [InlineData(null, false)]
    public void OnlyTotemAndAutoPagamentoUseLegacy80LargeSelfServiceProfile(
        string? module,
        bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "IsLegacy80LargeSelfServiceModule",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "IsLegacy80LargeSelfServiceModule");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { module }) ?? false));
    }

    [Theory]
    [InlineData("android.widget.Button{... app:id/dialog_button}", true)]
    [InlineData("android.widget.Button{... android:id/button1}", true)]
    [InlineData("android.widget.Button{... app:id/btn_confirmar}", false)]
    [InlineData("", false)]
    public void FinalOkFallbackRequiresKnownDialogResource(string activityDump, bool expected)
    {
        var method = typeof(SmartUiAutomationService).GetMethod(
            "ContainsSynchronizationDialogResource",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "ContainsSynchronizationDialogResource");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { activityDump }) ?? false));
    }

    [Fact]
    public void K2FinalDialogRequiresAnAdditionalWindowForCurrentActivity()
    {
        const string activity =
            "softcom.mobile.smart2/softcom.mobile.smart.views.activities.device.EmpresaAddActivity";
        var method = typeof(SmartUiAutomationService).GetMethod(
            "HasAdditionalWindowForActivity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "HasAdditionalWindowForActivity");

        var withoutDialog = $"Window #6 Window{{abc u0 {activity}}}";
        var withDialog = $$"""
            Window #6 Window{abc u0 {{activity}}}
            Window #5 Window{def u0 {{activity}}}
            Window #4 Window{ghi u0 softcom.mobile.smart2/softcom.mobile.smart.views.activities.EmpresaAddConfigActivity}
            """;

        Assert.False((bool)(method.Invoke(null, new object?[] { withoutDialog, activity }) ?? true));
        Assert.True((bool)(method.Invoke(null, new object?[] { withDialog, activity }) ?? false));
    }

    [Fact]
    public void K2Smart81ComposeDialogRequiresTwoWindowsFromSamePackage()
    {
        const string packageName = "softcom.mobile.smart2.redeflex";
        var method = typeof(SmartUiAutomationService).GetMethod(
            "HasAdditionalWindowForPackage",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(SmartUiAutomationService).FullName,
                "HasAdditionalWindowForPackage");

        var withoutDialog =
            $"Window #2 Window{{abc u0 {packageName}/softcom.mobile.smart.views.activities.loginnew.AuthActivity}}";
        var withDialog = $$"""
            Window #3 Window{abc u0 {{packageName}}}
            Window #2 Window{def u0 {{packageName}}/softcom.mobile.smart.views.activities.loginnew.AuthActivity}
            Window #1 Window{ghi u0 com.woyou.launcher/com.android.launcher3.Launcher}
            """;

        Assert.False((bool)(method.Invoke(null, new object?[] { withoutDialog, packageName }) ?? true));
        Assert.True((bool)(method.Invoke(null, new object?[] { withDialog, packageName }) ?? false));
    }
}
