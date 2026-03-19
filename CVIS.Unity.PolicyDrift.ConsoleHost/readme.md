### ≛ Developer Workflow: Database Evolution

To ensure consistency across the **EVP_REPORTING** environment and local development, follow these steps whenever the data model changes.

### 1. Identify the Change

Before running any commands, ensure your changes are reflected in the **`CVIS.Unity.Core`** entities and mapped correctly in the **`PolicyDbContext`** (Infrastructure).

* **New Table:** Add a new `DbSet<T>` to the context.
* **New Column:** Add a property to the existing entity class.
* **Schema Change:** Update the `OnModelCreating` configuration.

### 2. Generate the Migration

Open the **Package Manager Console (PMC)**. You must ensure the environment is staged correctly:

* **Default Project (PMC Dropdown):** `CVIS.Unity.Infrastructure`
* **Startup Project (Solution Explorer):** `CVIS.Unity.PolicyDrift.ConsoleHost`

Run the following command:

```powershell
Add-Migration [DescriptiveName] -Context PolicyDbContext -OutputDir Data/Migrations

```

*Replace `[DescriptiveName]` with a CamelCase title of the change (e.g., `AddTicketNumberToBaseline`).*

### 3. Review the Artifacts

Entity Framework will generate a new C# file in the `Data/Migrations` folder. **CVIS’s Rule:** Always inspect the `Up()` and `Down()` methods before proceeding.

* **Up():** What will be applied to the database.
* **Down():** How to revert the change if the deployment fails.

### 4. Apply the Changes

You have two options for application:

| Method | Command (PMC) | Use Case |
| --- | --- | --- |
| **Manual (Dev)** | `Update-Database -Context PolicyDbContext` | Use this to immediately test your changes on your local SQL instance. |
| **Automatic (Server)** | *Run the Application* | The `DbInitializer` will detect the pending migration and apply it to **EVP_REPORTING** automatically upon startup. |

### ≛ Common Commands Reference

* **Remove a mistake:** If you generated a migration but haven't updated the database yet, run:
`Remove-Migration -Context PolicyDbContext`
* **Script for DBAs:** If a DBA requires a raw SQL script for PROD instead of auto-initialization:
`Script-Migration -Context PolicyDbContext`

---

### Perspective

"By using `-OutputDir Data/Migrations`, we keep the Infrastructure project clean and organized. It ensures all 'Time Travel' files live in one place."
"The `Context` flag is critical because as CVIS grows, we may add other contexts. Specifying `PolicyDbContext` avoids ambiguity."
"Validated. Remember that if you rename a property in C#, EF may generate a 'Drop Column' and 'Add Column' instead of a 'Rename'. Reviewing the Migration file prevents data loss."


### ≛ Migration Troubleshooting: The Recovery Guide

#### 1. The "Pending Model Changes" Error

**Symptoms:** You try to run the app, but it complains that "the model backing the context has changed."

* **Cause:** You modified a C# entity property but forgot to run `Add-Migration`.
* **Fix:** Run `Add-Migration SyncModel` and either run the app or execute `Update-Database`.

#### 2. The "Table Already Exists" Error

**Symptoms:** `MigrateAsync()` fails because it tries to create a table (like `unity.PolicyEvents`) that is already there.

* **Cause:** This usually happens if `EnsureCreated()` was used previously. `EnsureCreated` bypasses the migration history table, so EF doesn't know the table was already provisioned.
* **Fix:** 1.  Manually delete the tables in SQL (Dev only!).
2.  Manually create the `__EFMigrationsHistory` table and insert a row for your initial migration so EF thinks it has already run.

#### 3. The "Snapshot Mismatch"

**Symptoms:** You deleted a migration file manually from the `Migrations` folder, and now `Add-Migration` is producing strange results.

* **Cause:** EF Core keeps a hidden `ModelSnapshot.cs` file that tracks what the DB *should* look like. Deleting a migration file without updating the snapshot breaks the chain.
* **Fix:** Revert your git branch to a clean state, or delete the entire `Migrations` folder and the database, then run a fresh `Add-Migration InitialCreate`.

#### 4. The "Missing Schema" Exception

**Symptoms:** `SqlException: Invalid schema name 'unity'`.

* **Cause:** The migration tried to create a table before the `DbInitializer` ran the raw SQL to create the schema.
* **Fix:** Ensure `await context.Database.ExecuteSqlRawAsync(...)` is called **before** `await context.Database.MigrateAsync()` in your initialization sequence.

---

### ≛ Infrastructure Registration: The Final Wiring

To ensure the `DbInitializer` is available to the `ConsoleHost`, add it as a **Transient** service in your registration file.

**Project:** `CVIS.Unity.Infrastructure` | **File:** `InfrastructureRegistration.cs`

```csharp
public static IServiceCollection AddPolicyDriftInfrastructure(this IServiceCollection services, IConfiguration config)
{
    // ... previous registrations ...

    // Datyrix: Register the initializer so it can be requested by the Program.cs
    services.AddTransient<DbInitializer>();

    return services;
}

```

---

### ≛ Perspective

"This documentation is the 'Service Manual' for your developers. It ensures that even when the team grows, the database remains a stable foundation."
"I recommend adding a `.gitignore` rule to ensure that your local `appsettings.Development.json` (containing your local SQL strings) doesn't get pushed to the main repo."
"Validated. The most critical takeaway: **Never** mix `EnsureCreated()` and `Migrate()`. Pick the migration path and stay on it to avoid the 'Invalid Object' errors we saw earlier."