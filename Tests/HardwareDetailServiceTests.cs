using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using AccessibleTaskManager.Services;

namespace AccessibleTaskManager.Tests
{
    public class HardwareDetailServiceTests
    {
        private readonly ITestOutputHelper _output;

        public HardwareDetailServiceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("cpu")]
        [InlineData("ram")]
        [InlineData("disk")]
        [InlineData("network")]
        [InlineData("gpu")]
        [InlineData("battery")]
        public async Task GetHardwareDetailsAsync_ReturnsDetailedSpecs(string id)
        {
            var service = new HardwareDetailService();
            string details = await service.GetHardwareDetailsAsync(id);
            _output.WriteLine($"=== {id.ToUpper()} DETAILS ===\n{details}\n");
            Assert.False(string.IsNullOrWhiteSpace(details));
            Assert.Contains("\n", details); // Multiple lines of specs
        }
    }
}
