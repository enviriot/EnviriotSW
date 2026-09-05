///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
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

    /// <summary>Reads a document and applies it - all of it, or none of it.</summary>
    /// <remarks>Two passes. The first reads the XML into a plan and checks every name the document
    /// asks for; the second creates the topics. Nothing is created until the whole document has
    /// been read, so a document that cannot be applied leaves the tree exactly as it was.
    /// <para>It used to create as it walked. A name the tree cannot carry - a subscription
    /// wildcard, say - threw when the walk reached it, and everything before that point stayed:
    /// half a package installed from the catalog, half a file uploaded from the IDE, and no way
    /// for the caller to learn how far it got.</para>
    /// <para>What the first pass does NOT reject is a malformed "m" or "s" attribute: that is
    /// still a warning and the node is created without it, as before. Whether a document with one
    /// unreadable attribute should be refused whole is a separate decision, not this one.</para>
    /// </remarks>
    public static void Import(TextReader reader, string path) {
      XDocument doc;
      using(var r = new System.Xml.XmlTextReader(reader)) {
        doc = XDocument.Load(r);
      }
      if(string.IsNullOrEmpty(path) && doc.Root.Attribute("path") != null) {
        path = doc.Root.Attribute("path").Value;
      }
      if(string.IsNullOrEmpty(path)) {
        return;   // nothing to address the document at
      }
      Topic.CheckPath(path, "Import");
      Topic existing;
      Topic.root.Exist(path, out existing);
      Node plan = Prepare(doc.Root, existing, null);
      if(plan != null) {   // null means the document is not newer than what is already there
        Apply(plan, null, path);
      }
    }

    /// <summary>One node the document asks for: read and checked, not yet created.</summary>
    private sealed class Node {
      public string Name;          // null for the root, which is addressed by path
      public JSValue State;
      public JSValue Manifest;
      public readonly List<Node> Children = new List<Node>();
    }

    /// <summary>Reads one element and its subtree into a plan. Null when the document skips it.</summary>
    /// <param name="existing">The topic this element would land on, when there is one already -
    /// needed only for the version guard, and null all the way down for a subtree being created.</param>
    private static Node Prepare(XElement x, Topic existing, string name) {
      Version ver;
      bool setVersion;
      if(x.Attribute("ver") != null && Version.TryParse(x.Attribute("ver").Value, out ver)) {
        if(existing != null) {
          Version oldVer;
          var ov_js = existing.GetField("version");
          string ov_s;
          if(ov_js.Is<string>() && (ov_s = ov_js.Value as string) != null && ov_s.StartsWith("¤VR")
              && Version.TryParse(ov_s.Substring(3), out oldVer) && oldVer >= ver) {
            return null;   // don't import older version
          }
        }
        setVersion = true;
      } else {
        ver = default(Version);
        setVersion = false;
      }
      Node node = new Node { Name = name };
      if(x.Attribute("m") != null) {
        try {
          node.Manifest = JsLib.ParseJson(x.Attribute("m").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).m - {1}", x.ToString(), ex.Message);
        }
      }
      if(setVersion) {
        node.Manifest = JsLib.SetField(node.Manifest, "version", "¤VR" + ver.ToString());
      }
      if(x.Attribute("s") != null) {
        try {
          node.State = JsLib.ParseJson(x.Attribute("s").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).s - {1}", x.ToString(), ex.Message);
        }
      }
      foreach(var xNext in x.Elements("i")) {
        XAttribute n = xNext.Attribute("n");
        if(n == null) {
          continue;   // not addressable, and never was
        }
        Topic.CheckName(n.Value, "Import");
        Topic child = null;
        if(existing != null) {
          existing.Exist(n.Value, out child);
        }
        Node next = Prepare(xNext, child, n.Value);
        if(next != null) {
          node.Children.Add(next);
        }
      }
      return node;
    }

    private static void Apply(Node node, Topic owner, string path) {
      Topic cur = owner == null ? Topic.Declare(Topic.root, path) : Topic.Declare(owner, node.Name);
      Topic.Fill(cur, node.State, node.Manifest, null);
      for(int i = 0; i < node.Children.Count; i++) {
        Apply(node.Children[i], cur, null);
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
