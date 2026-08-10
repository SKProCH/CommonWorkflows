using System;
using Numerge;
// ReSharper disable TemplateIsNotCompileTimeConstantProblem

namespace Utils;

public class NumergeLogger : INumergeLogger
{
    public void Log(NumergeLogLevel level, string message)
    {
        switch (level)
        {
            case NumergeLogLevel.Info:
                Serilog.Log.Information(message);
                break;
            case NumergeLogLevel.Warning:
                Serilog.Log.Warning(message);
                break;
            case NumergeLogLevel.Error:
                Serilog.Log.Error(message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }
}