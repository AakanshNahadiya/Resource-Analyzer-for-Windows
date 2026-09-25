using System;
using System.Threading.Tasks;
using Xunit;
using AccessibleTaskManager.Services;

namespace AccessibleTaskManager.Tests
{
    public class SystemMonitorServiceTests
    {
        [Fact]
        public void ParseWifiInterfacesOutput_ConnectedInterface_ParsesCorrectly()
        {
            string sampleOutput = @"
There is 1 interface on the system: 

    Name                   : Wi-Fi
    Description            : Realtek RTL8822CE 802.11ac PCIe Adapter
    GUID                   : aecabee5-22e7-4dbc-87b1-71c5415497a7
    Physical address       : 10:6f:d9:51:a1:ab
    Interface type         : Primary
    State                  : connected
    SSID                   : MyHomeNetwork
    AP BSSID               : 8e:9c:5f:4e:b7:6d
    Band                   : 5 GHz
    Channel                : 149
    Radio type             : 802.11ac
    Authentication         : WPA3-Personal
    Signal                 : 95%
";

            var (ssid, band, signal) = SystemMonitorService.ParseWifiInterfacesOutput(sampleOutput);

            Assert.Equal("MyHomeNetwork", ssid);
            Assert.Equal("5 GHz", band);
            Assert.Equal("95%", signal);
        }

        [Fact]
        public void ParseWifiInterfacesOutput_DisconnectedInterface_ReturnsNulls()
        {
            string sampleOutput = @"
There is 1 interface on the system: 

    Name                   : Wi-Fi
    Description            : Realtek RTL8822CE 802.11ac PCIe Adapter
    State                  : disconnected
";

            var (ssid, band, signal) = SystemMonitorService.ParseWifiInterfacesOutput(sampleOutput);

            Assert.Null(ssid);
            Assert.Null(band);
            Assert.Null(signal);
        }

        [Fact]
        public void ParseWifiInterfacesOutput_EmptyOrInvalid_ReturnsNullsSafely()
        {
            var (ssid1, band1, signal1) = SystemMonitorService.ParseWifiInterfacesOutput(string.Empty);
            Assert.Null(ssid1);
            Assert.Null(band1);
            Assert.Null(signal1);

            var (ssid2, band2, signal2) = SystemMonitorService.ParseWifiInterfacesOutput("random garbage text");
            Assert.Null(ssid2);
            Assert.Null(band2);
            Assert.Null(signal2);
        }

        [Fact]
        public void FormatWifiSummaryTag_AllFieldsPresent_FormatsCleanly()
        {
            string tag = SystemMonitorService.FormatWifiSummaryTag("Office-WiFi", "5 GHz", "88%");
            Assert.Equal("Office-WiFi, 5 GHz, 88%", tag);
        }

        [Fact]
        public void FormatWifiSummaryTag_MissingBand_AppendsPercentIfMissing()
        {
            string tag = SystemMonitorService.FormatWifiSummaryTag("GuestNet", null, "75");
            Assert.Equal("GuestNet, 75%", tag);
        }

        [Fact]
        public void FormatWifiSummaryTag_AllNull_ReturnsEmpty()
        {
            string tag = SystemMonitorService.FormatWifiSummaryTag(null, null, null);
            Assert.Equal(string.Empty, tag);
        }

        [Fact]
        public void GetQuickNetworkSpeedSummary_DoesNotLeakWifiMetadata_RemainsSpeedOnly()
        {
            var service = new SystemMonitorService();
            // Perform sample
            var (summary, details, percent) = service.SampleNetwork();

            string quickSpeech = service.GetQuickNetworkSpeedSummary();

            // Quick speech format strictly: Network (Adapter): X down, Y up
            Assert.StartsWith("Network (", quickSpeech);
            Assert.Contains("down", quickSpeech);
            Assert.Contains("up", quickSpeech);
            // Must not contain comma-separated Wi-Fi band or signal percentages in quick speed summary
            Assert.DoesNotContain("GHz", quickSpeech);
            Assert.DoesNotContain("%", quickSpeech);
        }
    }
}
