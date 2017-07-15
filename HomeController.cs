using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;

namespace SecurityAnalyzer
{
    public class HomeController : Controller
    {
        public static List<KeyValuePair<IMethodSymbol, List<EnumUse>>> _data;
        public ActionResult Index()
        {
            return View(_data);
        }
    }
}
