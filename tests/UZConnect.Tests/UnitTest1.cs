using UZConnect.Core.Models;
using UZConnect.Core.Parsing;
using Xunit;

namespace UZConnect.Tests;

public class SmsSegmentTests
{
    [Fact]
    public void GsmSingleMessageIsOneSegment()
        => Assert.Equal(1, SmsSegmentCalculator.CountSegments(new string('a', 160)));

    [Fact]
    public void GsmMultipartUses153CharacterSegments()
    {
        Assert.Equal(2, SmsSegmentCalculator.CountSegments(new string('a', 161)));
        Assert.Equal(2, SmsSegmentCalculator.CountSegments(new string('a', 306)));
    }

    [Fact]
    public void UnicodeUsesSeventyCharacterLimit()
    {
        Assert.Equal(1, SmsSegmentCalculator.CountSegments(new string('\u0905', 70)));
        Assert.Equal(2, SmsSegmentCalculator.CountSegments(new string('\u0905', 71)));
    }

    [Fact]
    public void EmptyMessageHasNoSegments()
        => Assert.Equal(0, SmsSegmentCalculator.CountSegments(string.Empty));
}

public class AdbParserTests
{
    [Fact]
    public void ParsesReadyAndUnauthorizedDevices()
    {
        const string output = """
            List of devices attached
            TESTSERIAL000001       device product:msm8916_32_512 model:UZ801 device:msm8916_32_512 transport_id:1
            other                  unauthorized transport_id:2

            """;
        var devices = AdbDevicesParser.Parse(output);
        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].IsReady);
        Assert.Equal("UZ801", devices[0].Model);
        Assert.True(devices[1].IsUnauthorized);
    }
}

public class TelephonyParserTests
{
    [Fact]
    public void ParsesGsmSignalWithoutInventingUnknownValues()
    {
        var reading = TelephonyParser.ParseSignal("SignalStrength: 13 0 -120 -160 -120 -1 -1 99 2147483647 2147483647 2147483647 2147483647 2147483647 gsm|lte");
        Assert.Equal(-87, reading.Dbm);
        Assert.Equal("GSM RSSI", reading.Source);
    }

    [Fact]
    public void UnknownSignalIsNotAvailable()
    {
        var reading = TelephonyParser.ParseSignal("SignalStrength: 99 0 -120 -160 -120 -1 -1 99 2147483647 2147483647 -184 2147483647 2147483647 gsm|lte");
        Assert.Null(reading.Dbm);
    }

    [Fact]
    public void ParsesRegistrationAndDataState()
    {
        const string dump = """
            mServiceState=0 0 home Ncell Ncell 42902 LTE LTE CSS not supported -1 -1 RoamInd=-1 DefRoamInd=-1 EmergOnly=false
            mSignalStrength=SignalStrength: 13 0 -120 -160 -120 -1 -1 99 2147483647 2147483647 -184 2147483647 2147483647 gsm|lte
            mDataConnectionState=2
            mDataConnectionApn=web
            mDataConnectionPossible=true
            """;
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gsm.operator.alpha"] = "Ncell",
            ["gsm.network.type"] = "LTE"
        };
        var snapshot = TelephonyParser.Parse(dump, props);
        Assert.True(snapshot.IsRegistered);
        Assert.Equal("Connected", snapshot.DataState);
        Assert.Equal("web", snapshot.DataApn);
        Assert.Equal("Ncell", snapshot.OperatorAlpha);
    }
}

public class UssdTextLocalizerTests
{
    [Fact]
    public void TranslatesStockChineseMmiError()
    {
        var text = "\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216 MMI \u7801\u65E0\u6548\u3002\n\u786E\u5B9A";
        var localized = UssdTextLocalizer.Localize(text);
        Assert.Equal("Connection problem or invalid MMI code.", localized);
        Assert.DoesNotContain("\u786E\u5B9A", localized);
    }
}

public class UssdTextLocalizerExtraTests
{
    [Fact]
    public void TranslatesChineseMmiDialogAndDropsOkButton()
    {
        var result = UssdTextLocalizer.Localize("\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216 MMI \u7801\u65E0\u6548\u3002\u786E\u5B9A");
        Assert.Equal("Connection problem or invalid MMI code.", result);
        Assert.True(UssdTextLocalizer.IsFailure(result));
    }

    [Fact]
    public void KeepsEnglishOnlyNetworkPayload()
    {
        const string payload = "Your balance is Rs. 64.62";
        Assert.Equal(payload, UssdTextLocalizer.Localize(payload));
        Assert.False(UssdTextLocalizer.IsFailure(payload));
    }
}

public class UssdCusdParserTests
{
    [Fact]
    public void ParsesBalanceAndMenuResponse()
    {
        const string response = "AT+CUSD=1,\"*901#\",15\r\r\nOK\r\n\r\n+CUSD: 1,\"Balance : Rs. 60.44 Expiry date: 2026-12-05.\r\n1 >Rs50-Unlimited Data+100SMS-1D\",15\r\n";
        var parsed = UssdCusdParser.Parse(response);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.Status);
        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.NeedsReply);
        Assert.Contains("Rs. 60.44", parsed.Text);
    }

    [Fact]
    public void ReturnsNullForNonCusdText()
    {
        Assert.Null(UssdCusdParser.Parse("OK\r\n"));
    }

    [Fact]
    public void HandlesEscapedQuotes()
    {
        const string response = "+CUSD: 0,\"Your plan is \\\"Data\\\" today\",15";
        var parsed = UssdCusdParser.Parse(response);
        Assert.NotNull(parsed);
        Assert.Equal("Your plan is \"Data\" today", parsed!.Text);
    }
}

public class OperatorDefaultsTests
{
    [Fact]
    public void NtcUsesNtcBalanceAndApn()
    {
        Assert.True(OperatorDefaults.IsNtc("Namaste"));
        Assert.True(OperatorDefaults.IsNtc("Nepal Telecom"));
        Assert.Equal("*400#", OperatorDefaults.DefaultBalanceCode("Namaste"));
        Assert.Contains(OperatorDefaults.UssdCommands("Nepal Telecom"),
            item => item.Code == "*1415#");
        var apn = OperatorDefaults.DefaultApn("Namaste");
        Assert.NotNull(apn);
        Assert.Equal("NTC", apn.Value.Name);
        Assert.Equal("ntc", apn.Value.Apn);
        Assert.True(OperatorDefaults.ApnMatches("ntnet", "Nepal Telecom"));
    }

    [Fact]
    public void NcellUsesWebApnAnd901Balance()
    {
        Assert.True(OperatorDefaults.IsNcell("Ncell"));
        Assert.Equal("*901#", OperatorDefaults.DefaultBalanceCode("Ncell"));
        var apn = OperatorDefaults.DefaultApn("Ncell");
        Assert.NotNull(apn);
        Assert.Equal("Ncell", apn.Value.Name);
        Assert.Equal("web", apn.Value.Apn);
        Assert.True(OperatorDefaults.ApnMatches("web", "Ncell"));
    }
}

public class ContentQueryParserTests
{
    [Fact]
    public void ParsesMetadataRows()
    {
        const string output = "Row: 0 _id=42, thread_id=7, address=Ncell, date=1714125007000, read=1, status=0";
        var rows = ContentQueryParser.Parse(output);
        Assert.Single(rows);
        Assert.Equal(42, rows[0].GetLong("_id"));
        Assert.Equal("Ncell", rows[0].Get("address"));
    }

    [Fact]
    public void PreservesBodyCommasAndNewlines()
    {
        const string output = "Row: 0 body=Hello, world\nsecond line\n";
        Assert.Equal("Hello, world\nsecond line", ContentQueryParser.ExtractSingleValue(output, "body"));
    }
}


