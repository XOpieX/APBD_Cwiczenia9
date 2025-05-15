using System.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/api/warehouse", async ([FromBody] WarehouseRequest request) =>
{
    if (request.Amount <= 0)
        return Results.BadRequest("Amount must be greater than 0");

    using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    
    using var transaction = connection.BeginTransaction();
    
    try
    {
        var productExists = await CheckIfExists(connection, transaction, "Product", request.IdProduct);
        var warehouseExists = await CheckIfExists(connection, transaction, "Warehouse", request.IdWarehouse);

        if (!productExists || !warehouseExists)
            return Results.NotFound($"Product or Warehouse not found");
        
        var orderId = await FindValidOrder(connection, transaction, request.IdProduct, request.Amount, request.CreatedAt);
        if (orderId == null)
            return Results.BadRequest("No valid order found for this product");

        var orderFulfilled = await CheckIfOrderFulfilled(connection, transaction, orderId.Value);
        if (orderFulfilled)
            return Results.BadRequest("Order has already been fulfilled");
        
        await UpdateOrderFulfilledAt(connection, transaction, orderId.Value);
        
        var price = await CalculateProductPrice(connection, transaction, request.IdProduct, request.Amount);
        var productWarehouseId = await InsertProductWarehouse(
            connection, transaction, 
            request.IdWarehouse, request.IdProduct, orderId.Value, 
            request.Amount, price, DateTime.UtcNow);

        transaction.Commit();
        
        return Results.Created($"/api/productwarehouse/{productWarehouseId}", new { Id = productWarehouseId });
    }
    catch (Exception ex)
    {
        transaction.Rollback();
        return Results.Problem($"Transaction failed: {ex.Message}");
    }
})
.WithName("AddProductToWarehouse")
.WithOpenApi();

app.MapPost("/api/warehouse/procedure", async ([FromBody] WarehouseRequest request) =>
{
    if (request.Amount <= 0)
        return Results.BadRequest("Amount must be greater than 0");

    using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    try
    {
        using var command = new SqlCommand("AddProductToWarehouse", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@IdProduct", request.IdProduct);
        command.Parameters.AddWithValue("@IdWarehouse", request.IdWarehouse);
        command.Parameters.AddWithValue("@Amount", request.Amount);
        command.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);

        var result = await command.ExecuteScalarAsync();
        
        if (result == null || result == DBNull.Value)
            return Results.BadRequest("Procedure execution failed");

        return Results.Created($"/api/productwarehouse/{result}", new { Id = result });
    }
    catch (SqlException ex)
    {
        return ex.Number switch
        {
            50000 => Results.BadRequest(ex.Message),
            _ => Results.Problem($"Database error: {ex.Message}")
        };
    }
})
.WithName("AddProductToWarehouseViaProcedure")
.WithOpenApi();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
            new WeatherForecast
            (
                DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                Random.Shared.Next(-20, 55),
                summaries[Random.Shared.Next(summaries.Length)]
            ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

async Task<bool> CheckIfExists(SqlConnection connection, SqlTransaction transaction, string tableName, int id)
{
    var query = $"SELECT 1 FROM {tableName} WHERE Id{tableName} = @Id";
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@Id", id);
    return await command.ExecuteScalarAsync() != null;
}

async Task<int?> FindValidOrder(SqlConnection connection, SqlTransaction transaction, int productId, int amount, DateTime createdAt)
{
    var query = @"
        SELECT TOP 1 IdOrder FROM [Order] 
        WHERE IdProduct = @IdProduct AND Amount = @Amount 
        AND CreatedAt < @CreatedAt
        ORDER BY CreatedAt DESC";
    
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@IdProduct", productId);
    command.Parameters.AddWithValue("@Amount", amount);
    command.Parameters.AddWithValue("@CreatedAt", createdAt);
    
    var result = await command.ExecuteScalarAsync();
    return result != null ? (int)result : null;
}

async Task<bool> CheckIfOrderFulfilled(SqlConnection connection, SqlTransaction transaction, int orderId)
{
    var query = "SELECT 1 FROM Product_Warehouse WHERE IdOrder = @IdOrder";
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@IdOrder", orderId);
    return await command.ExecuteScalarAsync() != null;
}

async Task UpdateOrderFulfilledAt(SqlConnection connection, SqlTransaction transaction, int orderId)
{
    var query = "UPDATE [Order] SET FulfilledAt = @FulfilledAt WHERE IdOrder = @IdOrder";
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@IdOrder", orderId);
    command.Parameters.AddWithValue("@FulfilledAt", DateTime.UtcNow);
    await command.ExecuteNonQueryAsync();
}

async Task<decimal> CalculateProductPrice(SqlConnection connection, SqlTransaction transaction, int productId, int amount)
{
    var query = "SELECT Price FROM Product WHERE IdProduct = @IdProduct";
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@IdProduct", productId);
    var price = (decimal)await command.ExecuteScalarAsync();
    return price * amount;
}

async Task<int> InsertProductWarehouse(
    SqlConnection connection, SqlTransaction transaction,
    int warehouseId, int productId, int orderId,
    int amount, decimal price, DateTime createdAt)
{
    var query = @"
        INSERT INTO Product_Warehouse 
        (IdWarehouse, IdProduct, IdOrder, Amount, Price, CreatedAt)
        OUTPUT INSERTED.IdProductWarehouse
        VALUES (@IdWarehouse, @IdProduct, @IdOrder, @Amount, @Price, @CreatedAt)";
    
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@IdWarehouse", warehouseId);
    command.Parameters.AddWithValue("@IdProduct", productId);
    command.Parameters.AddWithValue("@IdOrder", orderId);
    command.Parameters.AddWithValue("@Amount", amount);
    command.Parameters.AddWithValue("@Price", price);
    command.Parameters.AddWithValue("@CreatedAt", createdAt);
    
    return (int)await command.ExecuteScalarAsync();
}
public record WarehouseRequest(
    int IdProduct,
    int IdWarehouse,
    int Amount,
    DateTime CreatedAt);

public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}