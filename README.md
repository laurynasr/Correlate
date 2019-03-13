[![Build status](https://ci.appveyor.com/api/projects/status/rwfdg9d4i3g0qyga?svg=true)](https://ci.appveyor.com/project/skwasjer/correlate)
[![NuGet](https://img.shields.io/nuget/v/Correlate.svg)](https://www.nuget.org/packages/Correlate/)
[![Tests](https://img.shields.io/appveyor/tests/skwasjer/Correlate.svg)](https://ci.appveyor.com/project/skwasjer/correlate/build/tests)

# Correlate

Correlate provides flexible .NET Core support for correlation ID in ASP.NET Core and HttpClient.

| Package | Description | 
|---|---|
| Correlate.Abstractions | Abstractions library. |
| Correlate.Core | Core library, including a `DelegatingHandler` for `HttpClient`. |
| Correlate.AspNetCore | ASP.NET Core middleware. |
| Correlate.DependencyInjection | Extensions for registration in a `IServiceCollection` container. |

## Usage

In a typical ASP.NET Core (MVC) application, register the middleware and required services. Optionally, if using `HttpClient` to call other services, use `HttpClientFactory` to attach a delegating handler to a `HttpClient`, automatically adding a correlation id to the outgoing request for cross service correlation.

### Example ###

Add the packages:

```
dotnet add package Correlate.AspNetCore 
dotnet add package Correlate.DependencyInjection 
```

Configure your application:

```csharp
using Correlate.AspNetCore;
using Correlate.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Register services.
        services.AddCorrelate(options => 
            options.RequestHeaders = new []
            {
              // List of incoming headers possible. First that is set on given request is used and also returned in the response.
              "X-Correlation-ID",
              "Correlation-ID"
            }
        );

        // Register a typed client that will include the correlation id in outgoing request.
        services
            .AddHttpClient<IMyService, MyService>()
            .CorrelateRequests("X-Correlation-ID");

        services.AddMvcCore();
    }

    public void Configure(IApplicationBuilder app)
    {
        // Use middleware.
        app.UseCorrelate();

        app.UseMvc();
    }
}
```

> NOTE: This set of libraries is a work in progress.

## Logging

The Correlate middleware takes care of processing each incoming request for a correlation id. If no correlation is provided, one will be generated. Before the request continues down the pipeline, a log scope is created with a `CorrelationId` property, containing the correlation id.

## ICorrelationContextAccessor - Accessing the correlation id

To access the correlation id anywhere in code, inject an instance of `ICorrelationContextAccessor` in your constructor. 

### Example

```csharp
public class MyService
{
    public MyService(ICorrelationContextAccessor correlationContextAccessor)
    {
        string correlationId = correlationContextAccessor.CorrelationContext.CorrelationId;
    }
}
```

> Note: `correlationContextAccessor.CorrelationContext` will be null, when `MyService` is not scoped to a request. Thus, when used outside of ASP.NET (not using the middleware component), you are yourself responsible for creating the context  using `ICorrelationContextFactory`.

## ICorrelationContextFactory

This factory creates the correlation context and then sets it in the `ICorrelationContextAccessor`. If you wish to use the Correlate framework in for example a backend worker task (outside of ASP.NET Core pipeline), you can use this factory to scope individual tasks each with their own correlation context.

### Example

```csharp
public class MyWorker
{
    private readonly ICorrelationContextFactory _correlationContextFactory;
    private readonly ICorrelationIdFactory _correlationIdFactory;

    public MyWorker(ICorrelationContextFactory correlationContextFactory, ICorrelationIdFactory correlationIdFactory)
    {
        _correlationContextFactory = correlationContextFactory;
        _correlationIdFactory = correlationIdFactory;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            string newCorrelationId = _correlationIdFactory.Create();
            _correlationContextFactory.Create(newCorrelationId);
            try
            {
                await ExecuteAsync();
            }
            finally
            {
                _correlationContextFactory.Dispose();
            }

            await Task.Delay(5000);
        }
    }
}
```

> For parallel tasks a slightly different approach is necessary. Ensure that the context is created in each task individually instead of the above simplified example.

## ICorrelationIdFactory

By default, when generating a correlation id, the `GuidCorrelationIdFactory` will produce guid based correlation ids.

As an alternative, there's also a `RequestIdentifierCorrelationIdFactory` which produces base32 encoded correlation ids. To use this instead, simply register it manually in the service container.

### Example
```csharp
services.AddSingleton<ICorrelationIdFactory, RequestIdentifierCorrelationIdFactory>();
```

## More info

### Supported .NET targets
- .NET Standard 2.0
- ASP.NET Core 2.1/2.2

### Build requirements
- Visual Studio 2017
- .NET Core 2.2 SDK

#### Contributions
PR's are welcome. Please rebase before submitting, provide test coverage, and ensure the AppVeyor build passes. I will not consider PR's otherwise.

#### Contributors
- skwas (author/maintainer)
