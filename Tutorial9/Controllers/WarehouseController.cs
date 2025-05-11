using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Tutorial9.Controllers
{
    public class AddToWarehouseRequest
    {
        public int IdProduct { get; set; }
        public int IdWarehouse { get; set; }
        public int Amount { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class WarehouseController : ControllerBase
    {
        private readonly string _connectionString;

        public WarehouseController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // 1) Ręczne wykonanie scenariusza
        [HttpPost("manual")]
        public async Task<IActionResult> AddProductManual([FromBody] AddToWarehouseRequest req)
        {
            if (req.Amount <= 0)
                return BadRequest("Amount must be greater than zero.");

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1. Sprawdź istnienie produktu
            await using (var cmd = new SqlCommand("SELECT Price FROM Product WHERE IdProduct = @p", conn))
            {
                cmd.Parameters.AddWithValue("@p", req.IdProduct);
                var priceObj = await cmd.ExecuteScalarAsync();
                if (priceObj == null)
                    return BadRequest("Invalid IdProduct.");
            }

            // 2. Sprawdź istnienie magazynu
            await using (var cmd = new SqlCommand("SELECT 1 FROM Warehouse WHERE IdWarehouse = @w", conn))
            {
                cmd.Parameters.AddWithValue("@w", req.IdWarehouse);
                var ok = await cmd.ExecuteScalarAsync();
                if (ok == null)
                    return BadRequest("Invalid IdWarehouse.");
            }

            // 3. Znajdź niezrealizowane zamówienie
            int? orderId = null;
            decimal productPrice = 0;
            await using (var cmd = new SqlCommand(@"
                SELECT TOP 1 o.IdOrder, p.Price
                FROM [Order] o
                JOIN Product p ON p.IdProduct = o.IdProduct
                LEFT JOIN Product_Warehouse pw ON pw.IdOrder = o.IdOrder
                WHERE o.IdProduct = @p AND o.Amount = @a
                  AND pw.IdProductWarehouse IS NULL
                  AND o.CreatedAt < @now
                ORDER BY o.CreatedAt", conn))
            {
                cmd.Parameters.AddWithValue("@p", req.IdProduct);
                cmd.Parameters.AddWithValue("@a", req.Amount);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);

                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!rdr.Read())
                    return BadRequest("No matching order to fulfill.");
                
                orderId = rdr.GetInt32(0);
                productPrice = rdr.GetDecimal(1);
            }

            // 4–6. Transakcja: UPDATE + INSERT
            await using var tran = conn.BeginTransaction();
            try
            {
                // UPDATE Order
                await using (var cmdUpd = new SqlCommand(
                                 "UPDATE [Order] SET FulfilledAt = @now WHERE IdOrder = @o", conn, tran))
                {
                    cmdUpd.Parameters.AddWithValue("@now", DateTime.UtcNow);
                    cmdUpd.Parameters.AddWithValue("@o", orderId.Value);
                    await cmdUpd.ExecuteNonQueryAsync();
                }

                // INSERT Product_Warehouse
                int newId;
                await using (var cmdIns = new SqlCommand(@"
                    INSERT INTO Product_Warehouse
                      (IdWarehouse, IdProduct, IdOrder, Amount, Price, CreatedAt)
                    VALUES
                      (@w, @p, @o, @a, @total, @now);
                    SELECT SCOPE_IDENTITY();", conn, tran))
                {
                    cmdIns.Parameters.AddWithValue("@w", req.IdWarehouse);
                    cmdIns.Parameters.AddWithValue("@p", req.IdProduct);
                    cmdIns.Parameters.AddWithValue("@o", orderId.Value);
                    cmdIns.Parameters.AddWithValue("@a", req.Amount);
                    cmdIns.Parameters.AddWithValue("@total", req.Amount * productPrice);
                    cmdIns.Parameters.AddWithValue("@now", DateTime.UtcNow);

                    newId = Convert.ToInt32(await cmdIns.ExecuteScalarAsync());
                }

                tran.Commit();
                return Ok(new { NewId = newId });
            }
            catch (Exception ex)
            {
                tran.Rollback();
                return StatusCode(500, ex.Message);
            }
        }

        // 2) Wykonanie tej samej logiki przez procedurę składowaną
        [HttpPost("proc")]
        public async Task<IActionResult> AddProductViaProc([FromBody] AddToWarehouseRequest req)
        {
            if (req.Amount <= 0)
                return BadRequest("Amount must be greater than zero.");

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand("AddProductToWarehouse", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@IdProduct", req.IdProduct);
            cmd.Parameters.AddWithValue("@IdWarehouse", req.IdWarehouse);
            cmd.Parameters.AddWithValue("@Amount", req.Amount);
            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

            try
            {
                var newIdObj = await cmd.ExecuteScalarAsync();
                return newIdObj == null ? StatusCode(500, "Stored procedure did not return a new ID.") : Ok(new { NewId = Convert.ToInt32(newIdObj) });
            }
            catch (SqlException sqlEx)
            {
                // procedura rzuciła RAISERROR
                return BadRequest(sqlEx.Message);
            }
        }
    }
}
