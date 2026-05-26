using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services
    .AddAuthentication(DeviceAuthConstants.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        DeviceAuthConstants.Scheme,
        options => { });
builder.Services.AddAuthorization();
builder.Services.AddHbposApiServices();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var squareTokenSchemaInitializer = scope.ServiceProvider.GetRequiredService<ISquareTokenSchemaInitializer>();
    await squareTokenSchemaInitializer.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
