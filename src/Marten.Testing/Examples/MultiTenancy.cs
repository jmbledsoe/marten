﻿using System.Linq;
using Xunit;

namespace Marten.Testing.Examples
{
    public class MultiTenancy
    {
[Fact]
public void use_multiple_tenants()
{
    // Set up a basic DocumentStore with multi-tenancy
    // via a tenant_id column
    var store = DocumentStore.For(_ =>
    {
        // This sets up the DocumentStore to be multi-tenanted
        // by a tenantid column
        _.Connection(ConnectionSource.ConnectionString)
            .MultiTenanted();
    });

    // Write some User documents to tenant "tenant1"
    using (var session = store.OpenSession("tenant1"))
    {
        session.Store(new User{UserName = "Bill"});
        session.Store(new User{UserName = "Lindsey"});
        session.SaveChanges();
    }

    // Write some User documents to tenant "tenant2"
    using (var session = store.OpenSession("tenant2"))
    {
        session.Store(new User { UserName = "Jill" });
        session.Store(new User { UserName = "Frank" });
        session.SaveChanges();
    }

    // When you query for data from the "tenant1" tenant,
    // you only get data for that tenant
    using (var query = store.QuerySession("tenant1"))
    {
        query.Query<User>()
            .Select(x => x.UserName)
            .ToList()
            .ShouldHaveTheSameElementsAs("Bill", "Lindsey");
    }

    using (var query = store.QuerySession("tenant2"))
    {
        query.Query<User>()
                .Select(x => x.UserName)
                .ToList()
                .ShouldHaveTheSameElementsAs("Jill", "Frank");
    }
}
    }
}