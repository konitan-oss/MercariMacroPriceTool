using System;

namespace MercariMacroPriceTool.Automation;

public class StepFailedException : Exception
{
    public string StepName { get; }
    public int RetryUsed { get; }

    public StepFailedException(string stepName, int retryUsed, Exception inner)
        : base($"{stepName} failed after retries: {inner.Message}", inner)
    {
        StepName = stepName;
        RetryUsed = retryUsed;
    }
}
