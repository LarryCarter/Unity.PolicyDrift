using System.Data.Common;
using CVIS.Unity.Infrastructure.Diagnostics;
using NUnit.Framework;

namespace CVIS.Unity.Tests;

[TestFixture]
public class ExceptionClassifierTests
{
    [Test]
    public void Classify_UsesProviderNumber_WhenDbExceptionExposesIt()
    {
        var plan = ExceptionClassifier.Classify(new NumberedDbException(18456, 7));

        Assert.That(plan.InitialClassification, Is.EqualTo("SQL_AUTH_FAILURE"));
        Assert.That(plan.FailureLayer, Is.EqualTo("Authentication"));
    }

    [Test]
    public void Classify_UsesStandardErrorCode_WhenProviderHasNoNumber()
    {
        var plan = ExceptionClassifier.Classify(new StandardDbException(4060));

        Assert.That(plan.InitialClassification, Is.EqualTo("SQL_DATABASE_NOT_FOUND"));
        Assert.That(plan.FailureLayer, Is.EqualTo("Database"));
    }

    private sealed class NumberedDbException : DbException
    {
        public NumberedDbException(int number, int errorCode)
            : base("provider failure", errorCode) => Number = number;

        public int Number { get; }
    }

    private sealed class StandardDbException : DbException
    {
        public StandardDbException(int errorCode)
            : base("provider failure", errorCode)
        {
        }
    }
}
