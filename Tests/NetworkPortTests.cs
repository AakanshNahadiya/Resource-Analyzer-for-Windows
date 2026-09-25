using System;
using System.Linq;
using System.Threading.Tasks;
using AccessibleTaskManager.Models;
using AccessibleTaskManager.Services;
using Xunit;

namespace ResourceAnalyzer.Tests
{
    public class NetworkPortTests
    {
        [Fact]
        public void NetworkPortItem_FormatsListeningTcp_Correctly()
        {
            var item = new NetworkPortItem
            {
                Protocol = "TCP",
                LocalAddress = "0.0.0.0",
                LocalPort = 8080,
                RemoteAddress = "0.0.0.0",
                RemotePort = 0,
                State = "Listening",
                ProcessId = 14208,
                ProcessName = "python.exe",
                FriendlyName = "python.exe"
            };
            item.UpdateDisplayText();

            Assert.Equal("Port 8080 (TCP) - Listening - python.exe (PID 14208) [0.0.0.0:8080]", item.DisplayText);
            Assert.True(item.IsListening);
            Assert.False(item.IsEstablished);
            Assert.Equal("TCP:0.0.0.0:8080->0.0.0.0:0:14208", item.Key);
        }

        [Fact]
        public void NetworkPortItem_FormatsListeningTcp_WithFriendlyName()
        {
            var item = new NetworkPortItem
            {
                Protocol = "TCP",
                LocalAddress = "0.0.0.0",
                LocalPort = 5432,
                RemoteAddress = "0.0.0.0",
                RemotePort = 0,
                State = "Listening",
                ProcessId = 7780,
                ProcessName = "postgres.exe",
                FriendlyName = "PostgreSQL Server"
            };
            item.UpdateDisplayText();

            Assert.Equal("Port 5432 (TCP) - Listening - PostgreSQL Server (postgres.exe, PID 7780) [0.0.0.0:5432]", item.DisplayText);
        }

        [Fact]
        public void NetworkPortItem_FormatsListeningTcp_Ipv6()
        {
            var item = new NetworkPortItem
            {
                Protocol = "TCP",
                LocalAddress = "::",
                LocalPort = 5432,
                RemoteAddress = "::",
                RemotePort = 0,
                State = "Listening",
                ProcessId = 7780,
                ProcessName = "postgres.exe",
                FriendlyName = "postgres.exe"
            };
            item.UpdateDisplayText();

            Assert.Equal("Port 5432 (TCP) - Listening - postgres.exe (PID 7780) [[::]:5432]", item.DisplayText);
        }

        [Fact]
        public void NetworkPortItem_FormatsEstablishedTcp_Correctly()
        {
            var item = new NetworkPortItem
            {
                Protocol = "TCP",
                LocalAddress = "172.30.85.245",
                LocalPort = 51685,
                RemoteAddress = "140.82.112.21",
                RemotePort = 443,
                State = "Established",
                ProcessId = 18804,
                ProcessName = "copilot.exe",
                FriendlyName = "GitHub Copilot"
            };
            item.UpdateDisplayText();

            Assert.Equal("Port 51685 (TCP) -> 140.82.112.21:443 - Established - GitHub Copilot (copilot.exe, PID 18804)", item.DisplayText);
            Assert.True(item.IsEstablished);
            Assert.False(item.IsListening);
            Assert.Equal("TCP:172.30.85.245:51685->140.82.112.21:443:18804", item.Key);
        }

        [Fact]
        public void NetworkPortItem_FormatsUdp_Correctly()
        {
            var item = new NetworkPortItem
            {
                Protocol = "UDP",
                LocalAddress = "0.0.0.0",
                LocalPort = 5353,
                RemoteAddress = "*",
                RemotePort = 0,
                State = "Listening",
                ProcessId = 3120,
                ProcessName = "mDNSResponder.exe",
                FriendlyName = "Bonjour Service"
            };
            item.UpdateDisplayText();

            Assert.Equal("Port 5353 (UDP) - Listening - Bonjour Service (mDNSResponder.exe, PID 3120) [0.0.0.0:5353]", item.DisplayText);
            Assert.True(item.IsListening);
            Assert.Equal("UDP:0.0.0.0:5353:3120", item.Key);
        }

        [Theory]
        [InlineData(80, 0x5000)]
        [InlineData(443, 0xBB01)]
        [InlineData(5432, 0x3815)]
        [InlineData(8080, 0x901F)]
        public void EndiannessByteSwap_CorrectlyConvertsNetworkOrder(ushort expectedPort, uint networkOrderPort)
        {
            ushort swapped = (ushort)(((networkOrderPort & 0xFF) << 8) | ((networkOrderPort >> 8) & 0xFF));
            Assert.Equal(expectedPort, swapped);
        }

        [Fact]
        public async Task NetworkPortService_ReturnsLivePorts_WithoutException()
        {
            var service = new NetworkPortService();
            var ports = await service.GetNetworkPortsAsync();

            Assert.NotNull(ports);
            Assert.NotEmpty(ports);

            // There should be at least one listening port on any active Windows PC (e.g. RPC 135, SMB 445, DNS, etc.)
            bool hasListening = ports.Any(p => p.IsListening);
            Assert.True(hasListening, "Expected at least one listening port on Windows.");

            // Every item should have a valid local port > 0 and valid PID >= 0
            foreach (var p in ports)
            {
                Assert.True(p.LocalPort > 0, $"Local port must be > 0, got {p.LocalPort}");
                Assert.True(p.ProcessId >= 0, $"PID must be >= 0, got {p.ProcessId}");
                Assert.False(string.IsNullOrWhiteSpace(p.Protocol), "Protocol must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(p.DisplayText), "DisplayText must not be empty");
            }
        }
    }
}
