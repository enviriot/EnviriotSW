///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13 {
  public interface IPlugModul {
    void Init();
    void Start();
    void Tick();
    void Stop();

    /// <summary>The plugin's own node under /$YS, root of everything it configures.</summary>
    /// <remarks>Every implementation already kept this topic in a private field; it is here
    /// because <see cref="enabled"/> is read BEFORE Init() (Program.InitPlugins), so the getter
    /// has to resolve the topic itself rather than wait for Init to fill a field. Implementations
    /// are lazy for that reason - the property, not Init, is what guarantees a non-null value.
    /// </remarks>
    Topic Owner { get; }

    /// <summary>Whether the server runs this plugin at all, answered from <see cref="Owner"/>.</summary>
    /// <remarks>Read-only: switching a plugin off is done by writing false into its /$YS topic,
    /// which is Config-attributed and so survives a restart. The setter that used to mirror that
    /// write had no caller outside its own test - InitPlugins and StartPlugins only ever read it -
    /// and a second way to say the same thing is a second thing to keep in agreement.</remarks>
    bool enabled { get; }
  }
  public interface IPlugModulData {
    int priority { get; }
    string name { get; }
  }
}
