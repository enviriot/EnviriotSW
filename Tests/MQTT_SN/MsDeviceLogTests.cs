using System.Text;
using NUnit.Framework;
using X13.Periphery;

namespace X13.Tests {
  /// <summary>Записи журнала узла (.Log.Debug..Log.Error) попадают в журнал сервера текстом, без HEX-дампа.</summary>
  [TestFixture]
  public class MsDeviceLogTests {
    [TestCase("TWI0 addr=118")]
    [TestCase("T={21.5}")]
    [TestCase("")]
    public void PrintableRecordIsLoggedAsIs(string record) {
      Assert.That(MsDevice.LogText(Encoding.ASCII.GetBytes(record)), Is.EqualTo(record));
    }

    [Test]
    public void NonPrintableBytesBecomeDots() {
      Assert.That(MsDevice.LogText(new byte[] { 0x41, 0x00, 0x0A, 0x7F, 0xB0, 0x42 }), Is.EqualTo("A....B"));
    }

    [Test]
    public void MissingPayloadIsNamed() {
      Assert.That(MsDevice.LogText(null), Is.EqualTo("null"));
    }
  }
}
