using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations; // Добавляем для атрибутов

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddJsonFile("appsettings.json");

        // Добавляем логирование
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
            options.EnableSensitiveDataLogging(); // Для отладки SQL запросов
            options.LogTo(Console.WriteLine, LogLevel.Information); // Логируем SQL
        });

        // Добавляем поддержку сессий
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(30);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSession();

        // Глобальный обработчик ошибок (middleware)
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Глобальная ошибка при обработке запроса {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Внутренняя ошибка сервера\"}");
                }
            }
        });

        // === АВТОРИЗАЦИЯ ===
        app.MapPost("/api/login", async (ApplicationDbContext db, HttpContext http, LoginRequest login, ILogger<Program> logger) =>
        {
            try
            {
                logger.LogInformation("Попытка входа: {Login}", login?.Login);

                if (login == null || string.IsNullOrWhiteSpace(login.Login) || string.IsNullOrWhiteSpace(login.Password))
                {
                    logger.LogWarning("Неполные данные для входа");
                    return Results.BadRequest(new { error = "Логин и пароль обязательны" });
                }

                string inputLogin = login.Login.Trim();
                string inputPassword = login.Password.Trim();

                // Ищем среди мастеров
                var master = await db.Masters
                    .FirstOrDefaultAsync(m => m.Login != null && m.Login.Trim() == inputLogin &&
                                             m.Password != null && m.Password.Trim() == inputPassword);

                if (master != null)
                {
                    logger.LogInformation("Мастер найден: {Fio}, ID: {MasterID}", master.Fio, master.MasterID);
                    http.Session.SetString("UserId", master.MasterID.ToString());
                    http.Session.SetString("UserRole", "master");
                    http.Session.SetString("UserName", master.Fio ?? "");
                    http.Session.SetString("UserType", master.Type ?? "");
                    return Results.Ok(new
                    {
                        role = "master",
                        name = master.Fio,
                        id = master.MasterID,
                        type = master.Type
                    });
                }

                // Ищем среди клиентов
                var client = await db.Clients
                    .FirstOrDefaultAsync(c => c.Login != null && c.Login.Trim() == inputLogin &&
                                             c.Password != null && c.Password.Trim() == inputPassword);

                if (client != null)
                {
                    logger.LogInformation("Клиент найден: {Fio}, ID: {UserID}", client.Fio, client.UserID);
                    http.Session.SetString("UserId", client.UserID.ToString());
                    http.Session.SetString("UserRole", "client");
                    http.Session.SetString("UserName", client.Fio ?? "");
                    return Results.Ok(new
                    {
                        role = "client",
                        name = client.Fio,
                        id = client.UserID
                    });
                }

                logger.LogWarning("Пользователь не найден: {Login}", inputLogin);
                return Results.Unauthorized();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при входе пользователя {Login}", login?.Login);
                return Results.Problem("Ошибка при авторизации", statusCode: 500);
            }
        });

        app.MapPost("/api/logout", (HttpContext http, ILogger<Program> logger) =>
        {
            try
            {
                var userId = http.Session.GetString("UserId");
                logger.LogInformation("Выход пользователя ID: {UserId}", userId ?? "неизвестно");
                http.Session.Clear();
                return Results.Ok(new { message = "Выход выполнен успешно" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при выходе");
                return Results.Problem("Ошибка при выходе из системы", statusCode: 500);
            }
        });

        app.MapGet("/api/current-user", (HttpContext http, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var name = http.Session.GetString("UserName");
                var id = http.Session.GetString("UserId");
                var type = http.Session.GetString("UserType");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogDebug("Сессия не найдена или пользователь не авторизован");
                    return Results.NotFound(new { error = "Пользователь не авторизован" });
                }

                logger.LogDebug("Текущий пользователь: {Role} {Name} ID:{Id}", role, name, id);
                return Results.Ok(new { role, name, id, type });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении информации о текущем пользователе");
                return Results.Problem("Ошибка при получении данных", statusCode: 500);
            }
        });

        // === GET ALL ===
        app.MapGet("/api/clients", async (ApplicationDbContext db, HttpContext http, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к /api/clients", role);
                    return Results.Forbid();
                }

                var clients = await db.Clients.ToListAsync();
                logger.LogDebug("Получено {Count} клиентов", clients.Count);
                return Results.Ok(clients);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении списка клиентов");
                return Results.Problem("Ошибка при загрузке клиентов", statusCode: 500);
            }
        });

        app.MapGet("/api/masters", async (ApplicationDbContext db, ILogger<Program> logger) =>
        {
            try
            {
                var masters = await db.Masters.ToListAsync();
                logger.LogDebug("Получено {Count} мастеров", masters.Count);
                return Results.Ok(masters);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении списка мастеров");
                return Results.Problem("Ошибка при загрузке мастеров", statusCode: 500);
            }
        });

        app.MapGet("/api/requests", async (ApplicationDbContext db, HttpContext http, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogWarning("Попытка доступа без авторизации к /api/requests");
                    return Results.Unauthorized();
                }

                if (role == "master")
                {
                    var requests = await db.Requests.ToListAsync();
                    logger.LogDebug("Мастер получил {Count} заявок", requests.Count);
                    return Results.Ok(requests);
                }
                else if (role == "client" && !string.IsNullOrEmpty(userId))
                {
                    var clientId = int.Parse(userId);
                    var requests = await db.Requests.Where(r => r.ClientID == clientId).ToListAsync();
                    logger.LogDebug("Клиент {ClientId} получил {Count} своих заявок", clientId, requests.Count);
                    return Results.Ok(requests);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к /api/requests", role);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении списка заявок");
                return Results.Problem("Ошибка при загрузке заявок", statusCode: 500);
            }
        });

        app.MapGet("/api/comments", async (ApplicationDbContext db, HttpContext http, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogWarning("Попытка доступа без авторизации к /api/comments");
                    return Results.Unauthorized();
                }

                if (role == "master")
                {
                    var comments = await db.Comments.ToListAsync();
                    logger.LogDebug("Мастер получил {Count} комментариев", comments.Count);
                    return Results.Ok(comments);
                }
                else if (role == "client" && !string.IsNullOrEmpty(userId))
                {
                    var clientId = int.Parse(userId);
                    var clientRequests = await db.Requests
                        .Where(r => r.ClientID == clientId)
                        .Select(r => r.RequestID)
                        .ToListAsync();

                    var comments = await db.Comments
                        .Where(c => clientRequests.Contains(c.RequestID))
                        .ToListAsync();

                    logger.LogDebug("Клиент {ClientId} получил {Count} комментариев к своим заявкам", clientId, comments.Count);
                    return Results.Ok(comments);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к /api/comments", role);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении списка комментариев");
                return Results.Problem("Ошибка при загрузке комментариев", statusCode: 500);
            }
        });

        // === GET BY ID ===
        app.MapGet("/api/clients/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogWarning("Попытка доступа без авторизации к /api/clients/{id}", id);
                    return Results.Unauthorized();
                }

                if (role == "master" || (role == "client" && userId == id.ToString()))
                {
                    var client = await db.Clients.FindAsync(id);
                    if (client == null)
                    {
                        logger.LogWarning("Клиент с ID {ClientId} не найден", id);
                        return Results.NotFound(new { error = "Клиент не найден" });
                    }
                    logger.LogDebug("Получены данные клиента ID: {ClientId}", id);
                    return Results.Ok(client);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к клиенту ID:{ClientId}", role, id);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении клиента ID: {ClientId}", id);
                return Results.Problem("Ошибка при загрузке клиента", statusCode: 500);
            }
        });

        app.MapGet("/api/masters/{id}", async (ApplicationDbContext db, int id, ILogger<Program> logger) =>
        {
            try
            {
                var master = await db.Masters.FindAsync(id);
                if (master == null)
                {
                    logger.LogWarning("Мастер с ID {MasterId} не найден", id);
                    return Results.NotFound(new { error = "Мастер не найден" });
                }
                logger.LogDebug("Получены данные мастера ID: {MasterId}", id);
                return Results.Ok(master);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении мастера ID: {MasterId}", id);
                return Results.Problem("Ошибка при загрузке мастера", statusCode: 500);
            }
        });

        app.MapGet("/api/requests/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogWarning("Попытка доступа без авторизации к /api/requests/{id}", id);
                    return Results.Unauthorized();
                }

                var request = await db.Requests.FindAsync(id);
                if (request == null)
                {
                    logger.LogWarning("Заявка с ID {RequestId} не найдена", id);
                    return Results.NotFound(new { error = "Заявка не найдена" });
                }

                if (role == "master" || (role == "client" && request.ClientID.ToString() == userId))
                {
                    logger.LogDebug("Получены данные заявки ID: {RequestId}", id);
                    return Results.Ok(request);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к заявке ID:{RequestId}", role, id);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении заявки ID: {RequestId}", id);
                return Results.Problem("Ошибка при загрузке заявки", statusCode: 500);
            }
        });

        app.MapGet("/api/comments/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role))
                {
                    logger.LogWarning("Попытка доступа без авторизации к /api/comments/{id}", id);
                    return Results.Unauthorized();
                }

                var comment = await db.Comments.FindAsync(id);
                if (comment == null)
                {
                    logger.LogWarning("Комментарий с ID {CommentId} не найден", id);
                    return Results.NotFound(new { error = "Комментарий не найден" });
                }

                if (role == "master")
                {
                    logger.LogDebug("Мастер получил комментарий ID: {CommentId}", id);
                    return Results.Ok(comment);
                }
                else if (role == "client" && !string.IsNullOrEmpty(userId))
                {
                    var request = await db.Requests.FindAsync(comment.RequestID);
                    if (request != null && request.ClientID.ToString() == userId)
                    {
                        logger.LogDebug("Клиент получил комментарий ID: {CommentId}", id);
                        return Results.Ok(comment);
                    }
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к комментарию ID:{CommentId}", role, id);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при получении комментария ID: {CommentId}", id);
                return Results.Problem("Ошибка при загрузке комментария", statusCode: 500);
            }
        });

        // === POST ===
        app.MapPost("/api/clients", async (ApplicationDbContext db, HttpContext http, Client client, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к созданию клиента", role);
                    return Results.Forbid();
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(client.Fio) || string.IsNullOrWhiteSpace(client.Login) ||
                    string.IsNullOrWhiteSpace(client.Password))
                {
                    logger.LogWarning("Неполные данные клиента при создании");
                    return Results.BadRequest(new { error = "Все поля должны быть заполнены" });
                }

                // Проверка уникальности логина
                var existingClient = await db.Clients.FirstOrDefaultAsync(c => c.Login == client.Login);
                if (existingClient != null)
                {
                    logger.LogWarning("Логин {Login} уже занят", client.Login);
                    return Results.BadRequest(new { error = "Клиент с таким логином уже существует" });
                }

                await db.Clients.AddAsync(client);
                await db.SaveChangesAsync();

                logger.LogInformation("Создан новый клиент: {Fio}, ID: {UserID}", client.Fio, client.UserID);
                return Results.Ok(client);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при создании клиента");
                return Results.Problem("Ошибка при создании клиента", statusCode: 500);
            }
        });

        app.MapPost("/api/masters", async (ApplicationDbContext db, HttpContext http, Master master, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к созданию мастера", role);
                    return Results.Forbid();
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(master.Fio) || string.IsNullOrWhiteSpace(master.Login) ||
                    string.IsNullOrWhiteSpace(master.Password) || string.IsNullOrWhiteSpace(master.Type))
                {
                    logger.LogWarning("Неполные данные мастера при создании");
                    return Results.BadRequest(new { error = "Все поля должны быть заполнены" });
                }

                // Проверка уникальности логина
                var existingMaster = await db.Masters.FirstOrDefaultAsync(m => m.Login == master.Login);
                if (existingMaster != null)
                {
                    logger.LogWarning("Логин {Login} уже занят", master.Login);
                    return Results.BadRequest(new { error = "Мастер с таким логином уже существует" });
                }

                await db.Masters.AddAsync(master);
                await db.SaveChangesAsync();

                logger.LogInformation("Создан новый мастер: {Fio}, ID: {MasterID}", master.Fio, master.MasterID);
                return Results.Ok(master);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при создании мастера");
                return Results.Problem("Ошибка при создании мастера", statusCode: 500);
            }
        });

        app.MapPost("/api/requests", async (ApplicationDbContext db, HttpContext http, Request request, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(userId))
                {
                    logger.LogWarning("Попытка создания заявки без авторизации");
                    return Results.Unauthorized();
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(request.CarType) || string.IsNullOrWhiteSpace(request.CarModel) ||
                    string.IsNullOrWhiteSpace(request.ProblemDescryption))
                {
                    logger.LogWarning("Неполные данные заявки при создании");
                    return Results.BadRequest(new { error = "Все поля должны быть заполнены" });
                }

                if (role == "master")
                {
                    // Проверяем существование клиента и мастера
                    var clientExists = await db.Clients.AnyAsync(c => c.UserID == request.ClientID);
                    var masterExists = request.MasterID.HasValue && await db.Masters.AnyAsync(m => m.MasterID == request.MasterID);

                    if (!clientExists)
                    {
                        logger.LogWarning("Клиент с ID {ClientId} не существует", request.ClientID);
                        return Results.BadRequest(new { error = "Указанный клиент не существует" });
                    }

                    if (request.MasterID.HasValue && !masterExists)
                    {
                        logger.LogWarning("Мастер с ID {MasterId} не существует", request.MasterID);
                        return Results.BadRequest(new { error = "Указанный мастер не существует" });
                    }

                    await db.Requests.AddAsync(request);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Мастер создал заявку ID: {RequestId}", request.RequestID);
                    return Results.Ok(request);
                }
                else if (role == "client")
                {
                    request.ClientID = int.Parse(userId);
                    request.RequestStatus = "Новая заявка";
                    request.StartDate = DateTime.Now;

                    // Проверяем существование мастера, если указан
                    if (request.MasterID.HasValue)
                    {
                        var masterExists = await db.Masters.AnyAsync(m => m.MasterID == request.MasterID);
                        if (!masterExists)
                        {
                            logger.LogWarning("Клиент указал несуществующего мастера ID: {MasterId}", request.MasterID);
                            return Results.BadRequest(new { error = "Указанный мастер не существует" });
                        }
                    }

                    await db.Requests.AddAsync(request);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Клиент {ClientId} создал заявку ID: {RequestId}", request.ClientID, request.RequestID);
                    return Results.Ok(request);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к созданию заявки", role);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при создании заявки");
                return Results.Problem("Ошибка при создании заявки", statusCode: 500);
            }
        });

        app.MapPost("/api/comments", async (ApplicationDbContext db, HttpContext http, Comment comment, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (role != "master" || string.IsNullOrEmpty(userId))
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к созданию комментария", role);
                    return Results.Forbid();
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(comment.Message))
                {
                    logger.LogWarning("Пустой комментарий при создании");
                    return Results.BadRequest(new { error = "Комментарий не может быть пустым" });
                }

                // Проверяем существование заявки
                var requestExists = await db.Requests.AnyAsync(r => r.RequestID == comment.RequestID);
                if (!requestExists)
                {
                    logger.LogWarning("Заявка с ID {RequestId} не существует", comment.RequestID);
                    return Results.BadRequest(new { error = "Указанная заявка не существует" });
                }

                comment.MasterID = int.Parse(userId);
                await db.Comments.AddAsync(comment);
                await db.SaveChangesAsync();

                logger.LogInformation("Мастер {MasterId} создал комментарий ID: {CommentId} к заявке {RequestId}",
                    comment.MasterID, comment.CommentID, comment.RequestID);
                return Results.Ok(comment);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при создании комментария");
                return Results.Problem("Ошибка при создании комментария", statusCode: 500);
            }
        });

        // === PUT ===
        app.MapPut("/api/clients", async (ApplicationDbContext db, HttpContext http, Client clientData, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(userId))
                {
                    logger.LogWarning("Попытка обновления клиента без авторизации");
                    return Results.Unauthorized();
                }

                if (role == "master" || (role == "client" && userId == clientData.UserID.ToString()))
                {
                    var client = await db.Clients.FirstOrDefaultAsync(c => c.UserID == clientData.UserID);
                    if (client == null)
                    {
                        logger.LogWarning("Клиент с ID {ClientId} не найден", clientData.UserID);
                        return Results.NotFound(new { error = "Клиент не найден" });
                    }

                    // Валидация
                    if (string.IsNullOrWhiteSpace(clientData.Fio) || string.IsNullOrWhiteSpace(clientData.Phone) ||
                        string.IsNullOrWhiteSpace(clientData.Login) || string.IsNullOrWhiteSpace(clientData.Password))
                    {
                        logger.LogWarning("Неполные данные клиента при обновлении ID: {ClientId}", clientData.UserID);
                        return Results.BadRequest(new { error = "Все поля должны быть заполнены" });
                    }

                    // Проверка уникальности логина (если он изменился)
                    if (client.Login != clientData.Login)
                    {
                        var existingClient = await db.Clients.FirstOrDefaultAsync(c => c.Login == clientData.Login && c.UserID != clientData.UserID);
                        if (existingClient != null)
                        {
                            logger.LogWarning("Логин {Login} уже занят другим клиентом", clientData.Login);
                            return Results.BadRequest(new { error = "Клиент с таким логином уже существует" });
                        }
                    }

                    client.Fio = clientData.Fio;
                    client.Phone = clientData.Phone;
                    client.Login = clientData.Login;
                    client.Password = clientData.Password;

                    await db.SaveChangesAsync();
                    logger.LogInformation("Обновлен клиент ID: {ClientId}", client.UserID);
                    return Results.Json(client);
                }

                logger.LogWarning("Доступ запрещен для роли {Role} к обновлению клиента ID:{ClientId}", role, clientData.UserID);
                return Results.Forbid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обновлении клиента ID: {ClientId}", clientData?.UserID);
                return Results.Problem("Ошибка при обновлении клиента", statusCode: 500);
            }
        });

        app.MapPut("/api/masters", async (ApplicationDbContext db, HttpContext http, Master masterData, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (role != "master" || userId != masterData.MasterID.ToString())
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к обновлению мастера ID:{MasterId}", role, masterData?.MasterID);
                    return Results.Forbid();
                }

                var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterID == masterData.MasterID);
                if (master == null)
                {
                    logger.LogWarning("Мастер с ID {MasterId} не найден", masterData.MasterID);
                    return Results.NotFound(new { error = "Мастер не найден" });
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(masterData.Fio) || string.IsNullOrWhiteSpace(masterData.Phone) ||
                    string.IsNullOrWhiteSpace(masterData.Login) || string.IsNullOrWhiteSpace(masterData.Password) ||
                    string.IsNullOrWhiteSpace(masterData.Type))
                {
                    logger.LogWarning("Неполные данные мастера при обновлении ID: {MasterId}", masterData.MasterID);
                    return Results.BadRequest(new { error = "Все поля должны быть заполнены" });
                }

                // Проверка уникальности логина (если он изменился)
                if (master.Login != masterData.Login)
                {
                    var existingMaster = await db.Masters.FirstOrDefaultAsync(m => m.Login == masterData.Login && m.MasterID != masterData.MasterID);
                    if (existingMaster != null)
                    {
                        logger.LogWarning("Логин {Login} уже занят другим мастером", masterData.Login);
                        return Results.BadRequest(new { error = "Мастер с таким логином уже существует" });
                    }
                }

                master.Fio = masterData.Fio;
                master.Phone = masterData.Phone;
                master.Login = masterData.Login;
                master.Password = masterData.Password;
                master.Type = masterData.Type;

                await db.SaveChangesAsync();
                logger.LogInformation("Обновлен мастер ID: {MasterId}", master.MasterID);
                return Results.Json(master);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обновлении мастера ID: {MasterId}", masterData?.MasterID);
                return Results.Problem("Ошибка при обновлении мастера", statusCode: 500);
            }
        });

        app.MapPut("/api/requests", async (ApplicationDbContext db, HttpContext http, Request requestData, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");

                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к обновлению заявки", role);
                    return Results.Forbid();
                }

                var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestID == requestData.RequestID);
                if (request == null)
                {
                    logger.LogWarning("Заявка с ID {RequestId} не найдена", requestData.RequestID);
                    return Results.NotFound(new { error = "Заявка не найдена" });
                }

                // Валидация
                if (string.IsNullOrWhiteSpace(requestData.CarType) || string.IsNullOrWhiteSpace(requestData.CarModel) ||
                    string.IsNullOrWhiteSpace(requestData.ProblemDescryption) || string.IsNullOrWhiteSpace(requestData.RequestStatus))
                {
                    logger.LogWarning("Неполные данные заявки при обновлении ID: {RequestId}", requestData.RequestID);
                    return Results.BadRequest(new { error = "Все обязательные поля должны быть заполнены" });
                }

                // Проверка существования клиента и мастера
                var clientExists = await db.Clients.AnyAsync(c => c.UserID == requestData.ClientID);
                var masterExists = requestData.MasterID.HasValue && await db.Masters.AnyAsync(m => m.MasterID == requestData.MasterID);

                if (!clientExists)
                {
                    logger.LogWarning("Клиент с ID {ClientId} не существует при обновлении заявки", requestData.ClientID);
                    return Results.BadRequest(new { error = "Указанный клиент не существует" });
                }

                if (requestData.MasterID.HasValue && !masterExists)
                {
                    logger.LogWarning("Мастер с ID {MasterId} не существует при обновлении заявки", requestData.MasterID);
                    return Results.BadRequest(new { error = "Указанный мастер не существует" });
                }

                request.StartDate = requestData.StartDate;
                request.CarType = requestData.CarType;
                request.CarModel = requestData.CarModel;
                request.ProblemDescryption = requestData.ProblemDescryption;
                request.RequestStatus = requestData.RequestStatus;
                request.CompletionDate = requestData.CompletionDate;
                request.RepairParts = requestData.RepairParts;
                request.MasterID = requestData.MasterID;
                request.ClientID = requestData.ClientID;

                await db.SaveChangesAsync();
                logger.LogInformation("Обновлена заявка ID: {RequestId}", request.RequestID);
                return Results.Json(request);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обновлении заявки ID: {RequestId}", requestData?.RequestID);
                return Results.Problem("Ошибка при обновлении заявки", statusCode: 500);
            }
        });

        app.MapPut("/api/comments", async (ApplicationDbContext db, HttpContext http, Comment commentData, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (role != "master" || string.IsNullOrEmpty(userId))
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к обновлению комментария", role);
                    return Results.Forbid();
                }

                var comment = await db.Comments.FirstOrDefaultAsync(c => c.CommentID == commentData.CommentID);
                if (comment == null)
                {
                    logger.LogWarning("Комментарий с ID {CommentId} не найден", commentData.CommentID);
                    return Results.NotFound(new { error = "Комментарий не найден" });
                }

                if (comment.MasterID.ToString() != userId)
                {
                    logger.LogWarning("Мастер {UserId} пытается изменить чужой комментарий {CommentId}", userId, commentData.CommentID);
                    return Results.Forbid();
                }

                if (string.IsNullOrWhiteSpace(commentData.Message))
                {
                    logger.LogWarning("Пустой комментарий при обновлении ID: {CommentId}", commentData.CommentID);
                    return Results.BadRequest(new { error = "Комментарий не может быть пустым" });
                }

                comment.Message = commentData.Message;

                await db.SaveChangesAsync();
                logger.LogInformation("Обновлен комментарий ID: {CommentId}", comment.CommentID);
                return Results.Json(comment);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при обновлении комментария ID: {CommentId}", commentData?.CommentID);
                return Results.Problem("Ошибка при обновлении комментария", statusCode: 500);
            }
        });

        // === DELETE ===
        app.MapDelete("/api/clients/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к удалению клиента ID:{ClientId}", role, id);
                    return Results.Forbid();
                }

                // Проверяем, есть ли у клиента заявки
                var hasRequests = await db.Requests.AnyAsync(r => r.ClientID == id);
                if (hasRequests)
                {
                    logger.LogWarning("Невозможно удалить клиента ID:{ClientId} - есть связанные заявки", id);
                    return Results.BadRequest(new { error = "Невозможно удалить клиента, у которого есть заявки" });
                }

                var client = await db.Clients.FindAsync(id);
                if (client == null)
                {
                    logger.LogWarning("Клиент с ID {ClientId} не найден", id);
                    return Results.NotFound(new { error = "Клиент не найден" });
                }

                db.Clients.Remove(client);
                await db.SaveChangesAsync();

                logger.LogInformation("Удален клиент ID: {ClientId}, ФИО: {Fio}", id, client.Fio);
                return Results.Ok(new { message = "Клиент успешно удален" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при удалении клиента ID: {ClientId}", id);
                return Results.Problem("Ошибка при удалении клиента", statusCode: 500);
            }
        });

        app.MapDelete("/api/masters/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к удалению мастера ID:{MasterId}", role, id);
                    return Results.Forbid();
                }

                // Мастер может удалить только себя
                if (userId != id.ToString())
                {
                    logger.LogWarning("Мастер {UserId} пытается удалить другого мастера ID:{MasterId}", userId, id);
                    return Results.Forbid();
                }

                // Проверяем, есть ли у мастера заявки
                var hasRequests = await db.Requests.AnyAsync(r => r.MasterID == id);
                if (hasRequests)
                {
                    logger.LogWarning("Невозможно удалить мастера ID:{MasterId} - есть связанные заявки", id);
                    return Results.BadRequest(new { error = "Невозможно удалить мастера, который назначен на заявки" });
                }

                // Проверяем, есть ли комментарии
                var hasComments = await db.Comments.AnyAsync(c => c.MasterID == id);
                if (hasComments)
                {
                    logger.LogWarning("Невозможно удалить мастера ID:{MasterId} - есть комментарии", id);
                    return Results.BadRequest(new { error = "Невозможно удалить мастера, который оставил комментарии" });
                }

                var master = await db.Masters.FindAsync(id);
                if (master == null)
                {
                    logger.LogWarning("Мастер с ID {MasterId} не найден", id);
                    return Results.NotFound(new { error = "Мастер не найден" });
                }

                db.Masters.Remove(master);
                await db.SaveChangesAsync();

                logger.LogInformation("Удален мастер ID: {MasterId}, ФИО: {Fio}", id, master.Fio);
                return Results.Ok(new { message = "Мастер успешно удален" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при удалении мастера ID: {MasterId}", id);
                return Results.Problem("Ошибка при удалении мастера", statusCode: 500);
            }
        });

        app.MapDelete("/api/requests/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                if (role != "master")
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к удалению заявки ID:{RequestId}", role, id);
                    return Results.Forbid();
                }

                // Проверяем, есть ли комментарии к заявке
                var hasComments = await db.Comments.AnyAsync(c => c.RequestID == id);
                if (hasComments)
                {
                    logger.LogWarning("Невозможно удалить заявку ID:{RequestId} - есть комментарии", id);
                    return Results.BadRequest(new { error = "Невозможно удалить заявку, к которой есть комментарии" });
                }

                var request = await db.Requests.FindAsync(id);
                if (request == null)
                {
                    logger.LogWarning("Заявка с ID {RequestId} не найдена", id);
                    return Results.NotFound(new { error = "Заявка не найдена" });
                }

                db.Requests.Remove(request);
                await db.SaveChangesAsync();

                logger.LogInformation("Удалена заявка ID: {RequestId}", id);
                return Results.Ok(new { message = "Заявка успешно удалена" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при удалении заявки ID: {RequestId}", id);
                return Results.Problem("Ошибка при удалении заявки", statusCode: 500);
            }
        });

        app.MapDelete("/api/comments/{id}", async (ApplicationDbContext db, HttpContext http, int id, ILogger<Program> logger) =>
        {
            try
            {
                var role = http.Session.GetString("UserRole");
                var userId = http.Session.GetString("UserId");

                if (role != "master" || string.IsNullOrEmpty(userId))
                {
                    logger.LogWarning("Доступ запрещен для роли {Role} к удалению комментария ID:{CommentId}", role, id);
                    return Results.Forbid();
                }

                var comment = await db.Comments.FindAsync(id);
                if (comment == null)
                {
                    logger.LogWarning("Комментарий с ID {CommentId} не найден", id);
                    return Results.NotFound(new { error = "Комментарий не найден" });
                }

                if (comment.MasterID.ToString() != userId)
                {
                    logger.LogWarning("Мастер {UserId} пытается удалить чужой комментарий {CommentId}", userId, id);
                    return Results.Forbid();
                }

                db.Comments.Remove(comment);
                await db.SaveChangesAsync();

                logger.LogInformation("Удален комментарий ID: {CommentId}", id);
                return Results.Ok(new { message = "Комментарий успешно удален" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при удалении комментария ID: {CommentId}", id);
                return Results.Problem("Ошибка при удалении комментария", statusCode: 500);
            }
        });

        app.Run();
    }
}

// === Модели с атрибутами ===
public class LoginRequest
{
    public string? Login { get; set; }
    public string? Password { get; set; }
}

public class Client
{
    [Key] // Явно указываем первичный ключ
    public int UserID { get; set; }
    public string? Fio { get; set; }
    public string? Phone { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
}

public class Master
{
    [Key] // Явно указываем первичный ключ
    public int MasterID { get; set; }
    public string? Fio { get; set; }
    public string? Phone { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public string? Type { get; set; }
}

public class Request
{
    [Key] // Явно указываем первичный ключ
    public int RequestID { get; set; }
    public DateTime StartDate { get; set; }
    public string? CarType { get; set; }
    public string? CarModel { get; set; }
    public string? ProblemDescryption { get; set; }
    public string? RequestStatus { get; set; }
    public DateTime? CompletionDate { get; set; }
    public string? RepairParts { get; set; }
    public int? MasterID { get; set; }
    public int ClientID { get; set; }
}

public class Comment
{
    [Key] // Явно указываем первичный ключ
    public int CommentID { get; set; }
    public string? Message { get; set; }
    public int MasterID { get; set; }
    public int RequestID { get; set; }
}

// === Контекст базы данных ===
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : base(options)
    {
    }

    public DbSet<Client> Clients { get; set; }
    public DbSet<Master> Masters { get; set; }
    public DbSet<Request> Requests { get; set; }
    public DbSet<Comment> Comments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Настройка соответствия таблицам
        modelBuilder.Entity<Client>().ToTable("Client");
        modelBuilder.Entity<Master>().ToTable("Master");
        modelBuilder.Entity<Request>().ToTable("Requests");
        modelBuilder.Entity<Comment>().ToTable("Comments");

        // Настройка соответствия свойств столбцам (если в БД имена с маленькой буквы)
        modelBuilder.Entity<Client>(entity =>
        {
            entity.Property(e => e.UserID).HasColumnName("userID");
            entity.Property(e => e.Fio).HasColumnName("fio");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.Login).HasColumnName("login");
            entity.Property(e => e.Password).HasColumnName("password");
        });

        modelBuilder.Entity<Master>(entity =>
        {
            entity.Property(e => e.MasterID).HasColumnName("masterID");
            entity.Property(e => e.Fio).HasColumnName("fio");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.Login).HasColumnName("login");
            entity.Property(e => e.Password).HasColumnName("password");
            entity.Property(e => e.Type).HasColumnName("type");
        });

        modelBuilder.Entity<Request>(entity =>
        {
            entity.Property(e => e.RequestID).HasColumnName("requestID");
            entity.Property(e => e.StartDate).HasColumnName("startDate");
            entity.Property(e => e.CarType).HasColumnName("carType");
            entity.Property(e => e.CarModel).HasColumnName("carModel");
            entity.Property(e => e.ProblemDescryption).HasColumnName("problemDescryption");
            entity.Property(e => e.RequestStatus).HasColumnName("requestStatus");
            entity.Property(e => e.CompletionDate).HasColumnName("completionDate");
            entity.Property(e => e.RepairParts).HasColumnName("repairParts");
            entity.Property(e => e.MasterID).HasColumnName("masterID");
            entity.Property(e => e.ClientID).HasColumnName("clientID");
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.Property(e => e.CommentID).HasColumnName("commentID");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.MasterID).HasColumnName("masterID");
            entity.Property(e => e.RequestID).HasColumnName("requestID");
        });

        // Настройка связей
        modelBuilder.Entity<Request>()
            .HasOne<Master>()
            .WithMany()
            .HasForeignKey(r => r.MasterID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Request>()
            .HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.ClientID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne<Master>()
            .WithMany()
            .HasForeignKey(c => c.MasterID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne<Request>()
            .WithMany()
            .HasForeignKey(c => c.RequestID)
            .OnDelete(DeleteBehavior.Cascade);
    }
}