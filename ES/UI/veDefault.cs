///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace X13.UI {
  internal class veDefault : TextBlock, IValueEditor {
    public veDefault(InBase owner, JSC.JSValue type) {
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      ValueChanged(owner.value);
    }

    public void ValueChanged(JSC.JSValue value) {
      string rez=null;
      if(value == null) {
        rez = "";
      } else {
        if(value.ValueType == JSC.JSValueType.Object) {
          if(value.Value == null) {
            rez = "";
          } else {
            var sc = value["$type"];
            if((rez = sc.Value as string) == null) {
              rez = "Object";
            }
          }
        } else {
          rez = value.ToString();
        }
      }
      this.Text = rez;
    }

    public void TypeChanged(JSC.JSValue type) {
    }
  }
}
