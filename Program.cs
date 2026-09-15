using Npgsql;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// KESTREL
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(443, listenOptions =>
    {
        listenOptions.UseHttps("pfx", "pass");
    });
});

var app = builder.Build();

// CONFIG
string connStr = "Host=127.0.0.1;Port=5432;Username=YourUN;Password=YourPass;Database=yourDataBase";
string merchantPass2 = "AVlhq4KT4tr67hlISvF4";   // Пароль кассы 2

// ROUTES
app.MapPost("/robokassa/notify", async (HttpRequest request) =>
{
    try
    {
        var form = await request.ReadFormAsync();

        // Логирование
        Console.WriteLine("RoboKassa POST DATA:");
        foreach (var kv in form)
            Console.WriteLine($"{kv.Key} = {kv.Value}");

        // Основные параметры, которые всегда приходят
        string? outSum = form["OutSum"];
        string? invId = form["InvId"];
        string? signature = form["SignatureValue"].ToString().ToLowerInvariant();


        // ВАЛИДАЦИЯ
        if (string.IsNullOrWhiteSpace(outSum) ||
            string.IsNullOrWhiteSpace(invId) ||
            string.IsNullOrWhiteSpace(signature))
        {
            Console.WriteLine("Отсутствуют обязательные параметры");
            return Results.BadRequest("bad parameters");
        }

        // ПРОВЕРКА ПОДПИСИ
        string signString = $"{outSum}:{invId}:{merchantPass2}";
        string computedSign = Md5(signString).ToLowerInvariant();

        Console.WriteLine($"Computed signature: {computedSign} | Received: {signature}");

        if (computedSign != signature)
        {
            Console.WriteLine("Invalid signature");
            return Results.BadRequest("bad sign");
        }

        // Парсим сумму (может прийти как 150.00 или 150)
        if (!decimal.TryParse(outSum, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out decimal amountPaid))
        {
            Console.WriteLine("Невозможно распарсить сумму");
            return Results.BadRequest("invalid amount");
        }

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Обновляем платеж
        await using var cmd = new NpgsqlCommand(@"
            UPDATE payments
            SET 
                status       = 'paid',
                paid_at      = NOW(),
                amount_paid  = @amt
            WHERE order_id = @oid 
              AND status   = 'pending'
            RETURNING tg_id;
        ", conn);

        cmd.Parameters.AddWithValue("oid", invId);
        cmd.Parameters.AddWithValue("amt", amountPaid);

        var tgIdObj = await cmd.ExecuteScalarAsync();

        if (tgIdObj == null)
        {
            Console.WriteLine($"Платёж уже обработан или не найден: InvId = {invId}");
            // Всё равно отвечаем OK — чтобы RoboKassa не спамила повторно
            return Results.Content($"OK{invId}", "text/plain");
        }

        long tgId = Convert.ToInt64(tgIdObj);

        Console.WriteLine($"Успешно обработан платёж для tg_id = {tgId}, сумма {amountPaid}");

        // Обязательный ответ для RoboKassa
        return Results.Content($"OK{invId}", "text/plain");
    }
    catch (Exception ex)
    {
        Console.WriteLine("RoboKassa notify ERROR:");
        Console.WriteLine(ex);
        return Results.StatusCode(500);
    }
});

app.Run();

// HELPERS
static string Md5(string input)
{
    using var md5 = MD5.Create();
    byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hashBytes).ToLowerInvariant();
}