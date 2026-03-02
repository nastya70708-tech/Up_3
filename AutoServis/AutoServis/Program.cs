using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection.Emit;


// ---------- Программа и эндпоинты ----------
var builder = WebApplication.CreateBuilder(args);

// Добавляем DbContext (пример подключения к SQL Server, строку подключения указать в appsettings.json)
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// === CRUD для Client ===
app.MapGet("/api/clients", async (ApplicationDbContext db) =>
    await db.Clients.ToListAsync());

app.MapGet("/api/clients/{id:int}", async (int id, ApplicationDbContext db) =>
    await db.Clients.FindAsync(id) is Client client ? Results.Ok(client) : Results.NotFound());

app.MapPost("/api/clients", async (ClientCreateDto dto, ApplicationDbContext db) =>
{
    // Проверка уникальности логина
    if (await db.Clients.AnyAsync(c => c.Login == dto.Login))
        return Results.BadRequest("Login already exists");

    var client = new Client
    {
        FIO = dto.FIO,
        Phone = dto.Phone,
        Login = dto.Login,
        Password = dto.Password // в реальном проекте пароль нужно хешировать!
    };

    db.Clients.Add(client);
    await db.SaveChangesAsync();
    return Results.Created($"/api/clients/{client.UserID}", client);
});

app.MapPut("/api/clients/{id:int}", async (int id, ClientUpdateDto dto, ApplicationDbContext db) =>
{
    var client = await db.Clients.FindAsync(id);
    if (client is null) return Results.NotFound();

    client.FIO = dto.FIO;
    client.Phone = dto.Phone;
    client.Password = dto.Password; // хешировать

    await db.SaveChangesAsync();
    return Results.Ok(client);
});

app.MapDelete("/api/clients/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var client = await db.Clients.FindAsync(id);
    if (client is null) return Results.NotFound();

    // Проверка, есть ли связанные заявки (можно не удалять каскадно, запретим)
    if (await db.Requests.AnyAsync(r => r.ClientID == id))
        return Results.BadRequest("Client has requests, cannot delete");

    db.Clients.Remove(client);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// === CRUD для Master ===
app.MapGet("/api/masters", async (ApplicationDbContext db) =>
    await db.Masters.ToListAsync());

app.MapGet("/api/masters/{id:int}", async (int id, ApplicationDbContext db) =>
    await db.Masters.FindAsync(id) is Master master ? Results.Ok(master) : Results.NotFound());

app.MapPost("/api/masters", async (MasterCreateDto dto, ApplicationDbContext db) =>
{
    if (await db.Masters.AnyAsync(m => m.Login == dto.Login))
        return Results.BadRequest("Login already exists");

    var master = new Master
    {
        FIO = dto.FIO,
        Phone = dto.Phone,
        Login = dto.Login,
        Password = dto.Password,
        Type = dto.Type
    };

    db.Masters.Add(master);
    await db.SaveChangesAsync();
    return Results.Created($"/api/masters/{master.MasterID}", master);
});

app.MapPut("/api/masters/{id:int}", async (int id, MasterUpdateDto dto, ApplicationDbContext db) =>
{
    var master = await db.Masters.FindAsync(id);
    if (master is null) return Results.NotFound();

    master.FIO = dto.FIO;
    master.Phone = dto.Phone;
    master.Password = dto.Password;
    master.Type = dto.Type;

    await db.SaveChangesAsync();
    return Results.Ok(master);
});

app.MapDelete("/api/masters/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var master = await db.Masters.FindAsync(id);
    if (master is null) return Results.NotFound();

    if (await db.Requests.AnyAsync(r => r.MasterID == id))
        return Results.BadRequest("Master has requests, cannot delete");

    if (await db.Comments.AnyAsync(c => c.MasterID == id))
        return Results.BadRequest("Master has comments, cannot delete");

    db.Masters.Remove(master);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// === CRUD для Request ===
app.MapGet("/api/requests", async (ApplicationDbContext db) =>
    await db.Requests
        .Include(r => r.Master)
        .Include(r => r.Client)
        .ToListAsync());

app.MapGet("/api/requests/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var request = await db.Requests
        .Include(r => r.Master)
        .Include(r => r.Client)
        .FirstOrDefaultAsync(r => r.RequestID == id);
    return request is null ? Results.NotFound() : Results.Ok(request);
});

app.MapPost("/api/requests", async (RequestCreateDto dto, ApplicationDbContext db) =>
{
    // Проверка существования мастера и клиента
    var master = await db.Masters.FindAsync(dto.MasterID);
    if (master is null) return Results.BadRequest("Master not found");

    var client = await db.Clients.FindAsync(dto.ClientID);
    if (client is null) return Results.BadRequest("Client not found");

    var request = new Request
    {
        StartDate = dto.StartDate,
        CarType = dto.CarType,
        CarModel = dto.CarModel,
        ProblemDescription = dto.ProblemDescription,
        RequestStatus = dto.RequestStatus,
        CompletionDate = dto.CompletionDate,
        RepairParts = dto.RepairParts,
        MasterID = dto.MasterID,
        ClientID = dto.ClientID
    };

    db.Requests.Add(request);
    await db.SaveChangesAsync();
    return Results.Created($"/api/requests/{request.RequestID}", request);
});

app.MapPut("/api/requests/{id:int}", async (int id, RequestUpdateDto dto, ApplicationDbContext db) =>
{
    var request = await db.Requests.FindAsync(id);
    if (request is null) return Results.NotFound();

    // Проверка существования связанных сущностей
    if (!await db.Masters.AnyAsync(m => m.MasterID == dto.MasterID))
        return Results.BadRequest("Master not found");
    if (!await db.Clients.AnyAsync(c => c.ClientID == dto.ClientID))
        return Results.BadRequest("Client not found");

    request.StartDate = dto.StartDate;
    request.CarType = dto.CarType;
    request.CarModel = dto.CarModel;
    request.ProblemDescription = dto.ProblemDescription;
    request.RequestStatus = dto.RequestStatus;
    request.CompletionDate = dto.CompletionDate;
    request.RepairParts = dto.RepairParts;
    request.MasterID = dto.MasterID;
    request.ClientID = dto.ClientID;

    await db.SaveChangesAsync();
    return Results.Ok(request);
});

app.MapDelete("/api/requests/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var request = await db.Requests.FindAsync(id);
    if (request is null) return Results.NotFound();

    // Comments удалятся каскадно (OnDelete.Cascade)
    db.Requests.Remove(request);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// === CRUD для Comment ===
app.MapGet("/api/comments", async (ApplicationDbContext db) =>
    await db.Comments
        .Include(c => c.Master)
        .Include(c => c.Request)
        .ToListAsync());

app.MapGet("/api/comments/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var comment = await db.Comments
        .Include(c => c.Master)
        .Include(c => c.Request)
        .FirstOrDefaultAsync(c => c.CommentID == id);
    return comment is null ? Results.NotFound() : Results.Ok(comment);
});

app.MapPost("/api/comments", async (CommentCreateDto dto, ApplicationDbContext db) =>
{
    var master = await db.Masters.FindAsync(dto.MasterID);
    if (master is null) return Results.BadRequest("Master not found");

    var request = await db.Requests.FindAsync(dto.RequestID);
    if (request is null) return Results.BadRequest("Request not found");

    var comment = new Comment
    {
        Message = dto.Message,
        MasterID = dto.MasterID,
        RequestID = dto.RequestID
    };

    db.Comments.Add(comment);
    await db.SaveChangesAsync();
    return Results.Created($"/api/comments/{comment.CommentID}", comment);
});

app.MapPut("/api/comments/{id:int}", async (int id, CommentUpdateDto dto, ApplicationDbContext db) =>
{
    var comment = await db.Comments.FindAsync(id);
    if (comment is null) return Results.NotFound();

    // Проверка мастера
    if (!await db.Masters.AnyAsync(m => m.MasterID == dto.MasterID))
        return Results.BadRequest("Master not found");

    comment.Message = dto.Message;
    comment.MasterID = dto.MasterID;
    // RequestID не меняем

    await db.SaveChangesAsync();
    return Results.Ok(comment);
});

app.MapDelete("/api/comments/{id:int}", async (int id, ApplicationDbContext db) =>
{
    var comment = await db.Comments.FindAsync(id);
    if (comment is null) return Results.NotFound();

    db.Comments.Remove(comment);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

public class Client
{
    public int UserID { get; set; }
    public string FIO { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // Навигационное свойство
    public ICollection<Request> Requests { get; set; } = new List<Request>();
}

public class Master
{
    public int MasterID { get; set; }
    public string FIO { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // специализация

    // Навигационные свойства
    public ICollection<Request> Requests { get; set; } = new List<Request>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}

public class Request
{
    public int RequestID { get; set; }
    public DateTime StartDate { get; set; }
    public string CarType { get; set; } = string.Empty;
    public string CarModel { get; set; } = string.Empty;
    public string ProblemDescription { get; set; } = string.Empty;
    public string RequestStatus { get; set; } = string.Empty;
    public DateTime? CompletionDate { get; set; }
    public string? RepairParts { get; set; }

    // Внешние ключи
    public int MasterID { get; set; }
    public int ClientID { get; set; }

    // Навигационные свойства
    public Master? Master { get; set; }
    public Client? Client { get; set; }
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}

public class Comment
{
    public int CommentID { get; set; }
    public string Message { get; set; } = string.Empty;

    // Внешние ключи
    public int MasterID { get; set; }
    public int RequestID { get; set; }

    // Навигационные свойства
    public Master? Master { get; set; }
    public Request? Request { get; set; }
}

// ---------- DbContext ----------
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Client> Clients { get; set; }
    public DbSet<Master> Masters { get; set; }
    public DbSet<Request> Requests { get; set; }
    public DbSet<Comment> Comments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Уникальность логинов
        modelBuilder.Entity<Client>()
            .HasIndex(c => c.Login)
            .IsUnique();

        modelBuilder.Entity<Master>()
            .HasIndex(m => m.Login)
            .IsUnique();

        // Связи
        modelBuilder.Entity<Request>()
            .HasOne(r => r.Master)
            .WithMany(m => m.Requests)
            .HasForeignKey(r => r.MasterID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Request>()
            .HasOne(r => r.Client)
            .WithMany(c => c.Requests)
            .HasForeignKey(r => r.ClientID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne(c => c.Master)
            .WithMany(m => m.Comments)
            .HasForeignKey(c => c.MasterID)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Comment>()
            .HasOne(c => c.Request)
            .WithMany(r => r.Comments)
            .HasForeignKey(c => c.RequestID)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ---------- DTO для операций создания/обновления ----------
public record ClientCreateDto(string FIO, string Phone, string Login, string Password);
public record ClientUpdateDto(string FIO, string Phone, string Password); // Логин обычно не меняют

public record MasterCreateDto(string FIO, string Phone, string Login, string Password, string Type);
public record MasterUpdateDto(string FIO, string Phone, string Password, string Type);

public record RequestCreateDto(
    DateTime StartDate,
    string CarType,
    string CarModel,
    string ProblemDescription,
    string RequestStatus,
    DateTime? CompletionDate,
    string? RepairParts,
    int MasterID,
    int ClientID);

public record RequestUpdateDto(
    DateTime StartDate,
    string CarType,
    string CarModel,
    string ProblemDescription,
    string RequestStatus,
    DateTime? CompletionDate,
    string? RepairParts,
    int MasterID,
    int ClientID);

public record CommentCreateDto(string Message, int MasterID, int RequestID);
public record CommentUpdateDto(string Message, int MasterID); // RequestID менять не разрешим (коммент остаётся при заявке)