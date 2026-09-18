using System.Reflection;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class SmartUiAutomationActivityTests
{
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
}
