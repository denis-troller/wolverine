using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.SqlServer.Transport;

public class data_operations : IAsyncLifetime
{
    public static int count = 0;
    
    private IHost _host;
    private SqlServerTransport theTransport;
    private IStatefulResource? theResource;
    private SqlServerQueue theQueue;
    private IMessageStore theMessageStore;

    public async Task InitializeAsync()
    {
        var schemaName = "sqlserver" + ++count;
        
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(schemaName);
        }
        
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schemaName)
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqlServerQueue("one");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var runtime = _host.GetRuntime();

        theMessageStore = runtime.Storage;
        
        theTransport = runtime.Options.Transports.OfType<SqlServerTransport>().Single();
        await theTransport.InitializeAsync(runtime);
        
        theTransport.TryBuildStatefulResource(runtime, out theResource).ShouldBeTrue();

        await theResource.Setup(CancellationToken.None);
        await theResource.ClearState(CancellationToken.None);

        theQueue = theTransport.Queues["one"];
    }
    
    
    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task send_not_scheduled_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);
        
        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task send_scheduled_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeTrue();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);
        
        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task delete_expired_smoke_test()
    {
        var databaseTime = await theTransport.SystemTimeAsync();
        
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = databaseTime.Subtract(1.Hours());
        await theQueue.SendAsync(envelope, CancellationToken.None);
        
        var envelope2 = ObjectMother.Envelope();
        envelope2.DeliverBy = databaseTime.Add(1.Hours());
        await theQueue.SendAsync(envelope2, CancellationToken.None);
        
        var envelope3 = ObjectMother.Envelope();
        envelope3.DeliverBy = null;
        await theQueue.SendAsync(envelope3, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(3);
        await theQueue.DeleteExpiredAsync(CancellationToken.None);
        (await theQueue.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task move_from_outgoing_to_queue_async()
    {
        (await theQueue.CountAsync()).ShouldBe(0);
        
        var envelope = ObjectMother.Envelope();
        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 0);

        await theQueue.MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);
        
        (await theQueue.CountAsync()).ShouldBe(1);

        var stats = await theMessageStore.Admin.FetchCountsAsync();
        stats.Outgoing.ShouldBe(0);
    }

    // TODO -- move from scheduled to queued
    // TODO -- pop off queue and move to inbox
    // TODO -- pop off queue like for buffered

}