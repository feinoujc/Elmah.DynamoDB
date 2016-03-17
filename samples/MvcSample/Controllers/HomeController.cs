using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using Elmah;

namespace MvcSample.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }

        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";

            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";

            return View();
        }

        public ActionResult Errors()
        {
            for (int i = 0; i < 100; i++)
            {
                ErrorSignal.FromCurrentContext().Raise(new SystemException("exception #" + i));
            }

            return RedirectToAction("Index");
        }
    }
}