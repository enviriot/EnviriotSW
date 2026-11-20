using System;
using System.IO;
using System.Reflection;
using LiteDB;
using NUnit.Framework;
using X13.Archivist;

namespace X13.Tests {
  [TestFixture]
  public class ArchStoreCountTests {
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    /// <summary>Страниц в пяти начальных сегментах кэша LiteDB: 12 + 50 + 100 + 500 + 1000.</summary>
    private const int BASE_CACHE_PAGES = 1662;

    private string _dir;

    [SetUp]
    public void SetUp() {
      _dir = Path.Combine(Path.GetTempPath(), "X13.ArchCount." + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
      if(Directory.Exists(_dir)) {
        Directory.Delete(_dir, true);
      }
    }

    [Test]
    public void RawCountCountsEverySeriesAcrossWindowBoundaries() {
      var day = TimeSpan.FromDays(1);
      using(var st = new ArchStore(_dir)) {
        st.Open();
        var a = st.Register("/a", 10, T0, T0);
        var b = st.Register("/b", 10, T0, T0);
        var h = st.Register("/h", 0.05, T0, T0);
        // Тик до конца первого окна, ровно его конец, пропуск в неделю и последний представимый момент.
        foreach(var t in new[] { T0, T0 + day - TimeSpan.FromTicks(1), T0 + day, T0 + day + day, T0.AddDays(9), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) }) {
          st.Append(a, t, 1);
        }
        for(int i = 0; i < 5; i++) {
          st.Append(b, T0.AddHours(i * 13), 2);
        }
        for(int i = 0; i < 3; i++) {
          st.Append(h, T0.AddMinutes(i), 3);
        }

        Assert.That(st.RawCount(false), Is.EqualTo(11));
        Assert.That(st.RawCount(true), Is.EqualTo(3));
      }
    }

    [Test]
    public void RawCountKeepsTheCacheBelowTheSizeOfTheIndex() {
      const int perDay = 6000, days = 70;
      using(var st = new ArchStore(_dir)) {
        st.Open();
        var a = st.Register("/a", 100, T0, T0);
        st.BeginBatch();
        for(int i = 0; i < perDay * days; i++) {
          st.Append(a, T0.AddTicks(i * (TimeSpan.TicksPerDay / perDay)), i);
        }
        st.EndBatch();
      }
      // Повторное открытие начинает с пустого кэша: дальше его размер определяет только подсчёт.
      using(var st = new ArchStore(_dir)) {
        st.Open();

        Assert.That(st.RawCount(false), Is.EqualTo(perDay * days));
        Assert.That(CachePages(st, "_raw"), Is.LessThanOrEqualTo(BASE_CACHE_PAGES));
      }
    }

    private static int CachePages(ArchStore st, string field) {
      var db = (LiteDatabase)typeof(ArchStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(st);
      using(var r = db.Execute("SELECT $ FROM $database")) {
        r.Read();
        return r.Current["cache"]["extendPages"].AsInt32;
      }
    }
  }
}
