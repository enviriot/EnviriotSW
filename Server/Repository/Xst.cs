///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace X13.Repository {
  /// <summary>XML import and export of a repository subtree - the .xst format.</summary>
  /// <remarks>Lifted out of Repo, where it was a third of the file. It is serialisation, not
  /// repository mechanics: it holds no state, runs no tick and is called from four places that
  /// have nothing to do with each other - startup configuration, the storage plugin's defaults,
  /// the catalog's package installer and the WebUI's upload/download.
  /// <para>Metadata travels JSON-encoded in the "m" attribute, state in "s", and "ver" carries an
  /// import guard: an element whose version is not newer than what the tree already holds is
  /// skipped entirely.</para></remarks>
  public static class Xst {
    public static bool Import(string fileName, string path = null) {
      if(string.IsNullOrEmpty(fileName) || !File.Exists(fileName)) {
        return false;
      }
      X13.Log.Info("Import {0}", Path.GetFullPath(fileName));
      using(StreamReader reader = File.OpenText(fileName)) {
        Import(reader, path);
      }
      return true;
    }
    public static void Import(TextReader reader, string path) {
      XDocument doc;
      using(var r = new System.Xml.XmlTextReader(reader)) {
        doc = XDocument.Load(r);
      }

      if(string.IsNullOrEmpty(path) && doc.Root.Attribute("path") != null) {
        path = doc.Root.Attribute("path").Value;
      }

      Import(doc.Root, null, path);
    }
    private static void Import(XElement xElement, Topic owner, string path) {
      if(xElement == null || ((xElement.Attribute("n") == null || owner == null) && path == null)) {
        return;
      }
      Version ver;
      Topic cur = null;
      bool setVersion;
      if(xElement.Attribute("ver") != null && Version.TryParse(xElement.Attribute("ver").Value, out ver)) {
        if(owner == null ? Topic.root.Exist(path, out cur) : owner.Exist(xElement.Attribute("n").Value, out cur)) {
          Version oldVer;
          var ov_js = cur.GetField("version");
          string ov_s;
          if(ov_js.Is<string>() && (ov_s = ov_js.Value as string) != null && ov_s.StartsWith("¤VR") && Version.TryParse(ov_s.Substring(3), out oldVer) && oldVer >= ver) {
            return; // don't import older version
          }
        }
        setVersion = true;
      } else {
        ver = default(Version);
        setVersion = false;
      }
      JSValue state = null, manifest = null;
      if(xElement.Attribute("m") != null) {
        try {
          manifest = JsLib.ParseJson(xElement.Attribute("m").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).m - {1}", xElement.ToString(), ex.Message);
        }
      }
      if(setVersion) {
        manifest = JsLib.SetField(manifest, "version", "¤VR" + ver.ToString());
      }

      if(xElement.Attribute("s") != null) {
        try {
          state = JsLib.ParseJson(xElement.Attribute("s").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).s - {1}", xElement.ToString(), ex.Message);
        }
      }


      if(owner == null) {
        cur = Topic.Declare(Topic.root, path);
      } else {
        cur = Topic.Declare(owner, xElement.Attribute("n").Value);
      }
      Topic.Fill(cur, state, manifest, null);
      foreach(var xNext in xElement.Elements("i")) {
        Import(xNext, cur, null);
      }
    }

    /// <summary>Writes the tree to a file, replacing it only once the new one is complete.</summary>
    /// <remarks>This was File.Create straight over the target, which truncates it before a single
    /// byte of the new document is written - so an export that failed part way, or a power cut
    /// during one, left behind exactly the truncated server.xst that Stop()'s guard below exists
    /// to avoid producing. Reproduced, not imagined: a failed export turned a configuration into a
    /// 0-byte file. How the swap itself is done, and why there is no fallback, is in Swap.</remarks>
    public static void Export(string filename, Topic t, bool configOnly) {
      if(filename == null) {
        throw new ArgumentNullException("filename");
      }
      string tmp = filename + ".tmp";
      try {
        using(FileStream stream = File.Create(tmp)) {
          Export(stream, t, configOnly);
        }
        Swap(tmp, filename);
      }
      catch {
        try {
          File.Delete(tmp);   // does not throw when it was never created
        }
        catch(Exception ex) {
          Log.Warning("Export({0}) - {1} could not be removed: {2}", filename, tmp, ex.Message);
        }
        throw;
      }
    }

    /// <summary>Puts the freshly written file in place of the one it replaces.</summary>
    /// <remarks>File.Replace and nothing else, deliberately. It is atomic - a crash during it
    /// leaves the old file or the new one, never half of either - and it fails when another
    /// process holds the file it has to remove, which a live server reported once as
    /// ERROR_UNABLE_TO_REMOVE_REPLACED and which cleared by itself minutes later.
    /// <para>A fallback that renames the old file aside instead was written for that case and then
    /// removed, because a negative control said it covers nothing: File.Replace still succeeds
    /// against a holder that permits deletion, and where it does not, a rename needs the very same
    /// access and fails too. The band between the two is empty. What answers a held file is not a
    /// second way to swap it but asking again later - see Repo.Tick, which reschedules the save
    /// rather than dropping it.</para>
    /// <para>Writing in place would always succeed against a reader. That is what this replaced,
    /// and it is what leaves a truncated server.xst after a power cut - a save delayed by thirty
    /// seconds is the better trade.</para></remarks>
    private static void Swap(string tmp, string filename) {
      if(File.Exists(filename)) {
        File.Replace(tmp, filename, null);
      } else {
        File.Move(tmp, filename);
      }
    }

    public static void Export(Stream stream, Topic t, bool configOnly) {
      if(stream == null) {
        throw new ArgumentNullException("stream");
       }
      XDocument doc = BuildExportDocument(t, configOnly);
      System.Xml.XmlTextWriter writer = new System.Xml.XmlTextWriter(stream, Encoding.UTF8);
      writer.Formatting = System.Xml.Formatting.Indented;
      writer.QuoteChar = '\'';
      doc.WriteTo(writer);
      writer.Flush();
    }
    private static XDocument BuildExportDocument(Topic t, bool configOnly) {
      if(t == null) {
        throw new ArgumentNullException("topic");
      }
      XDocument doc = new XDocument(new XElement("xst", new XAttribute("path", t.path)));
      doc.Declaration = new XDeclaration("1.0", "utf-8", "yes");
      var s = t.GetState();
      if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
        doc.Root.Add(new XAttribute("s", JsLib.Stringify(s)));
      }
      var m = t.GetField(null);
      doc.Root.Add(new XAttribute("m", JsLib.Stringify(m)));
      foreach(Topic c in t.children) {
        Export(doc.Root, c, configOnly);
      }
      return doc;
    }
    private static void Export(XElement x, Topic t, bool configOnly) {
      if(x == null || t == null) {
        return;
      }
      XElement xCur = new XElement("i", new XAttribute("n", t.name));
      foreach(Topic c in t.children) {
        Export(xCur, c, configOnly);
      }
      if(!configOnly || xCur.HasElements || t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
        var s = t.GetState();
        if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
          var state_json = JsLib.Stringify(s);
          if(state_json!=null) {
            xCur.Add(new XAttribute("s", state_json));
          }
        }

        var m = t.GetField(null);
        var manifest_json = JsLib.Stringify(m);
        if(manifest_json!=null){
          xCur.Add(new XAttribute("m", manifest_json));
        }

        x.Add(xCur);
      }
    }
  }
}
