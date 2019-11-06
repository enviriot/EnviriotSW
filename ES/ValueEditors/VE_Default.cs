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
  internal class VE_Default : TextBlock, IValueEditor {
    public VE_Default(InBase owner, JSC.JSValue type) { //-V3117
      base.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
      base.Padding = new System.Windows.Thickness(10, 0, 10, 0);
      ValueChanged(owner.Value);
    }

    public void ValueChanged(JSC.JSValue value) {
      string rez;
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
