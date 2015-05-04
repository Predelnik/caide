﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace slycelote.VsCaide.Utilities
{
    public class Logger
    {
        public static void LogError(string formatString, params object[] args)
        {
            var outputWindow = Services.GeneralOutputWindow;
            outputWindow.OutputStringThreadSafe("[VsCaide] " + string.Format(formatString, args));
            outputWindow.Activate();
        }

        public static void LogException(Exception e)
        {
            LogError("{0}", e.Message);
        }

        public static void LogMessage(string formatString, params object[] args)
        {
            var outputWindow = Services.GeneralOutputWindow;
            outputWindow.OutputStringThreadSafe("[VsCaide] " + string.Format(formatString, args));
        }

    }
}
