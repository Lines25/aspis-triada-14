using System.Linq;
using Content.Shared._Aspis.Organizations;
using NUnit.Framework;

namespace Content.Tests.Shared._Aspis;

[TestFixture]
public sealed class OrganizationFrontierCargoRulesTest
{
    [TestCase(20, 5, 1)]
    [TestCase(40, 10, 2)]
    [TestCase(60, 15, 3)]
    [TestCase(80, 20, 4)]
    public void WorkloadIsPreservedAcrossBatches(int total, int perPost, int expectedBatches)
    {
        Assert.That(OrganizationFrontierCargoRules.Valid(total), Is.True);
        var posts = OrganizationFrontierCargoRules.Destinations(total);
        Assert.That(posts, Is.EqualTo(new[] { perPost, perPost, perPost, perPost }));
        Assert.That(posts.Sum(), Is.EqualTo(total));
        var dispatched = 0;
        var batches = 0;
        while (dispatched < total)
        {
            var count = OrganizationFrontierCargoRules.NextBatch(total - dispatched, 60);
            Assert.That(count, Is.InRange(1, 20));
            dispatched += count;
            batches++;
        }
        Assert.That(dispatched, Is.EqualTo(total));
        Assert.That(batches, Is.EqualTo(expectedBatches));
    }

    [TestCase(19)]
    [TestCase(81)]
    public void InvalidWorkload(int total)
    {
        Assert.That(OrganizationFrontierCargoRules.Valid(total), Is.False);
    }

    [TestCase(80, 0, 0)]
    [TestCase(80, 7, 7)]
    [TestCase(80, 60, 20)]
    [TestCase(3, 60, 3)]
    public void QueueCapacityIsRespected(int remaining, int available, int expected)
    {
        Assert.That(OrganizationFrontierCargoRules.NextBatch(remaining, available), Is.EqualTo(expected));
    }
}
