///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
//using JSF = NiL.JS.Core.Functions;
using JSI = NiL.JS.Core.Interop;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public class TopicJS : JSI.CustomType, IDisposable {
    private Topic _owner;

    public TopicJS(Topic owner) {
      _owner = owner;
    }

    #region CustomType Members
    protected override JSC.JSValue GetProperty(JSC.JSValue key, bool forWrite, JSC.PropertyScope propertyScope) {
      return base.GetProperty(key, forWrite, propertyScope);
    }
    protected override void SetProperty(JSC.JSValue key, JSC.JSValue value, JSC.PropertyScope propertyScope, bool throwOnError) {
      base.SetProperty(key, value, propertyScope, throwOnError);
    }
    #endregion CustomType Members

    #region IDisposable Members
    public void Dispose() {
      // unsubscribe
    }
    #endregion IDisposable Members
  }
}
