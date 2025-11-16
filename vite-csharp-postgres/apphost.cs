#:package Aspire.Hosting.PostgreSQL@13.0.0
#:package Aspire.Hosting.JavaScript@13.0.0
#:package Aspire.Hosting.Docker@13-*
#:sdk Aspire.AppHost.Sdk@13.0.0

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("dc");

var postgres = builder.AddPostgres("postgres")
                      .WithPgAdmin()
                      .AddDatabase("db");

var api = builder.AddCSharpApp("api", "./api")
                 .WithHttpHealthCheck("/health")
                 .WithExternalHttpEndpoints()
                 .WaitFor(postgres)
                 .WithReference(postgres)
                 .WithUrls(context =>
                 {
                     foreach (var url in context.Urls)
                     {
                         url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
                     }

                     context.Urls.Add(new()
                     {
                         Url = "/scalar",
                         DisplayText = "API Reference",
                         Endpoint = context.GetEndpoint("https")
                     });
                 });

var frontend = builder.AddViteApp("frontend", "./frontend")
                      .WithReference(api)
                      .WithUrl("", "Todo UI");

api.PublishWithContainerFiles(frontend, "wwwroot");

builder.Build().Run();
