using System.Reflection;
using SoftcomSmartProvisioner.Services;

namespace SoftcomSmartProvisioner.Tests;

public sealed class AdbServiceTests
{
    [Fact]
    public void ShellCommandAlwaysTargetsExplicitSerial()
    {
        var args = AdbService.BuildShellArguments("ABC123", "getprop ro.product.model");
        Assert.Equal(new[] { "-s", "ABC123", "shell", "getprop ro.product.model" }, args);
    }

    [Fact]
    public void ParsesMultipleAdbDevices()
    {
        var serials = AdbService.ParseConnectedSerials("List of devices attached\r\nONE device product:x\r\nTWO device product:y\r\n");
        Assert.Equal(new[] { "ONE", "TWO" }, serials);
    }

    [Fact]
    public void RealSmartPackageOutranksOtherSoftcomApplications()
    {
        Assert.True(AdbService.SmartPackageScore("softcom.mobile.smart2") >
                    AdbService.SmartPackageScore("com.mycompany.softcomcollector"));
    }

    [Fact]
    public void NonSmartSoftcomApplicationDoesNotReachSmartThreshold()
    {
        Assert.True(AdbService.SmartPackageScore("com.mycompany.softcomcollector") < 60);
    }

    [Fact]
    public void VendorSmartPosInForegroundDoesNotOverrideSoftcomSmart()
    {
        var selected = AdbService.SelectPreferredSmartPackage(
            new[]
            {
                "com.mercadopago.smartpos",
                "softcom.mobile.smart2.redeflex",
                "softcom.mobile.smart2"
            },
            "com.mercadopago.smartpos");

        Assert.Equal("softcom.mobile.smart2", selected);
    }

    [Fact]
    public void ForegroundOnlyBreaksTieBetweenEquallyTrustedPackages()
    {
        var selected = AdbService.SelectPreferredSmartPackage(
            new[] { "vendor.mobile.smart.alpha", "vendor.mobile.smart.beta" },
            "vendor.mobile.smart.beta");

        Assert.Equal("vendor.mobile.smart.beta", selected);
    }

    [Fact]
    public void LongPressUsesExplicitTouchscreenAndKeepsGestureInsideButton()
    {
        var command = AdbService.BuildLongPressCommand(532, 852, 5000);

        Assert.Equal("input touchscreen swipe 532 852 533 852 5000", command);
    }

    [Theory]
    [InlineData("mShowRequested=true mInputShown=true mWindowVisible=false mIsInputViewShown=false", false)]
    [InlineData("mShowRequested=true mInputShown=true mWindowVisible=true mIsInputViewShown=true", true)]
    [InlineData("mInputShown=true", true)]
    [InlineData("mShowRequested=false mInputShown=false mWindowVisible=false", false)]
    public void KeyboardVisibilityUsesRenderedWindowInsteadOfStaleShowRequest(string dump, bool expected)
    {
        var method = typeof(AdbService).GetMethod(
            "IsSoftKeyboardActuallyVisible",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(AdbService).FullName, "IsSoftKeyboardActuallyVisible");

        Assert.Equal(expected, (bool)(method.Invoke(null, new object?[] { dump }) ?? false));
    }

}
