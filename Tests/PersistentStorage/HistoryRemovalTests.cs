using System;
using System.IO;
using LiteDB;
using NUnit.Framework;
using X13.PersistentStorage;

namespace X13.Tests {
  [TestFixture]
  public class HistoryRemovalTests : RepoTestBase {
    [Test]
    public void InitBacksUpThenDropsOnlyHistoryAndCanRunAgain() {
      string original = Environment.CurrentDirectory;
      string directory = Path.Combine(Path.GetTempPath(), "X13.HistoryRemoval." + Guid.NewGuid().ToString("N"));
      string bin = Path.Combine(directory, "bin");
      string data = Path.Combine(directory, "data");
      Directory.CreateDirectory(bin);
      Directory.CreateDirectory(data);
      string file = Path.Combine(data, "persist.ldb");
      var id = ObjectId.NewObjectId();
      try {
        using(var db = new LiteDatabase(file)) {
          db.UserVersion = 7;
          db.GetCollection("objects").Insert(new BsonDocument { ["_id"] = id, ["p"] = "/saved", ["v"] = new BsonDocument { ["attr"] = 4 } });
          db.GetCollection("states").Insert(new BsonDocument { ["_id"] = id, ["v"] = "unchanged" });
          db.GetCollection("history").Insert(new BsonDocument { ["m"] = "old log" });
        }
        Environment.CurrentDirectory = bin;
        for(int pass = 0; pass < 2; pass++) {
          string backups = Path.Combine(directory, "backup" + pass);
          root.Get("/$YS/PersistentStorage/bak").SetState(Str(backups));
          Tick();
          var storage = new LiteDB_Pl();
          try { storage.Init(); }
          finally { storage.Stop(); }
          using(var db = new LiteDatabase(file)) {
            Assert.That(db.CollectionExists("history"), Is.False);
            Assert.That(db.GetCollection("objects").FindById(id)["p"].AsString, Is.EqualTo("/saved"));
            Assert.That(db.GetCollection("states").FindById(id)["v"].AsString, Is.EqualTo("unchanged"));
            Assert.That(db.UserVersion, Is.EqualTo(7));
          }
          string[] copies = Directory.GetFiles(backups, "*.bak");
          Assert.That(copies.Length, Is.EqualTo(1));
          using(var backup = new LiteDatabase(copies[0])) {
            Assert.That(backup.CollectionExists("history"), Is.EqualTo(pass == 0));
          }
        }
      }
      finally {
        Environment.CurrentDirectory = original;
        Directory.Delete(directory, true);
      }
    }
  }
}
