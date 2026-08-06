using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FinSight.Data;
using FinSight.Models;
using FinSight.Helpers;

namespace FinSight.Controllers
{
    public class ExpensesController : BaseController
    {
        private readonly FinSightDbContext _db;
        private readonly ILogger<ExpensesController> _logger;

        public ExpensesController(FinSightDbContext db, ILogger<ExpensesController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ─────────────────────────────────────────────
        // RBAC helpers
        // ─────────────────────────────────────────────
        private bool CanManage => CurrentRoleID != null && Roles.CanManageExpenses(CurrentRoleID.Value);

        // ─────────────────────────────────────────────
        // GET: Expenses
        // ─────────────────────────────────────────────
        public async Task<IActionResult> Index(int? departmentId, string? status, DateTime? startDate, DateTime? endDate, string? search, int page = 1)
        {
            if (!IsAuthenticated) return RedirectToLogin();

            var roleId = CurrentRoleID ?? Roles.DepartmentHead;
            var tenantFilter = GetTenantFilter();

            try
            {
                if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseIndex"))
                {
                    return await RenderExpenseIndexCompatibilityAsync(
                        roleId,
                        tenantFilter,
                        departmentId,
                        status,
                        startDate,
                        endDate,
                        search,
                        page);
                }

            var query = _db.Expenses
                .AsNoTracking()
                .AsQueryable();

            if (tenantFilter != null)
                query = query.Where(e => e.TenantID == tenantFilter.Value);

            // Department Head can only see own department
            if (roleId == Roles.DepartmentHead)
            {
                var userDept = HttpContext.Session.GetInt32("DepartmentID");
                if (userDept != null)
                    query = query.Where(e => e.DepartmentID == userDept.Value);
            }
            else if (departmentId.HasValue && departmentId.Value > 0)
            {
                query = query.Where(e => e.DepartmentID == departmentId.Value);
            }

            if (!string.IsNullOrEmpty(status))
                query = query.Where(e => e.Status == status);

            if (startDate.HasValue)
                query = query.Where(e => e.ExpenseDate >= startDate.Value);

            if (endDate.HasValue)
                query = query.Where(e => e.ExpenseDate <= endDate.Value);

            if (!string.IsNullOrEmpty(search))
                query = query.Where(e => e.ExpenseTitle.Contains(search) || e.Category.Contains(search) || e.Description.Contains(search));

            // ── KPI Calculations ──
            var totalExpenses = await query.SumAsync(e => (decimal?)e.Amount) ?? 0m;
            var totalCount = await query.CountAsync();

            // Monthly expenses (current month)
            var now = DateTime.Now;
            var monthlyExpenses = await query
                .Where(e => e.ExpenseDate.Year == now.Year && e.ExpenseDate.Month == now.Month)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            // Remaining budget across all linked budgets
            var budgetIds = await query.Select(e => e.BudgetID).Distinct().ToListAsync();
            var totalAllocated = 0m;
            var totalSpent = 0m;
            if (budgetIds.Any())
            {
                totalAllocated = await _db.Budgets
                    .AsNoTracking()
                    .Where(b => budgetIds.Contains(b.BudgetID))
                    .SumAsync(b => (decimal?)b.Amount) ?? 0m;

                totalSpent = await _db.Expenses
                    .AsNoTracking()
                    .Where(e => budgetIds.Contains(e.BudgetID))
                    .SumAsync(e => (decimal?)e.Amount) ?? 0m;
            }
            var remainingBudget = totalAllocated - totalSpent;

            // ── Pagination ──
            int pageSize = 15;
            if (page < 1) page = 1;

            // Keep pagination in memory for compatibility with older SQL Server versions
            // that reject EF Core's OFFSET/FETCH SQL.
            var filteredRows = await query
                .OrderByDescending(e => e.ExpenseDate)
                .ThenByDescending(e => e.ExpenseID)
                .Select(e => new
                {
                    e.ExpenseID,
                    e.BudgetRequestID,
                    e.BudgetID,
                    e.DepartmentID,
                    e.TenantID,
                    e.ExpenseTitle,
                    e.Category,
                    e.Description,
                    e.Amount,
                    e.ExpenseDate,
                    e.Status,
                    e.CreatedAt,
                    DepartmentName = e.Department != null ? e.Department.DepartmentName : null
                })
                .ToListAsync();

            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (totalPages == 0) totalPages = 1;
            if (page > totalPages) page = totalPages;

            var items = filteredRows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(e => new Expense
                {
                    ExpenseID = e.ExpenseID,
                    BudgetRequestID = e.BudgetRequestID,
                    BudgetID = e.BudgetID,
                    DepartmentID = e.DepartmentID,
                    TenantID = e.TenantID,
                    ExpenseTitle = e.ExpenseTitle ?? string.Empty,
                    Category = e.Category ?? string.Empty,
                    Description = e.Description ?? string.Empty,
                    Amount = e.Amount,
                    ExpenseDate = e.ExpenseDate,
                    Status = e.Status ?? "Recorded",
                    CreatedAt = e.CreatedAt,
                    Department = string.IsNullOrWhiteSpace(e.DepartmentName)
                        ? null
                        : new Department
                        {
                            DepartmentID = e.DepartmentID,
                            DepartmentName = e.DepartmentName
                        }
                })
                .ToList();

            ViewBag.TotalExpenses = totalExpenses;
            ViewBag.MonthlyExpenses = monthlyExpenses;
            ViewBag.RemainingBudget = remainingBudget;
            ViewBag.TotalAllocated = totalAllocated;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalCount = totalCount;
            ViewBag.RoleID = roleId;
            ViewBag.CanManage = CanManage;

            // Preserve filter values
            ViewBag.CurrentDepartment = departmentId;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentStartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentEndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentSearch = search;

            if (roleId != Roles.DepartmentHead)
            {
                var depts = await _db.Departments
                    .AsNoTracking()
                    .Where(d => tenantFilter == null || d.TenantID == tenantFilter)
                    .OrderBy(d => d.DepartmentName)
                    .Select(d => new
                    {
                        d.DepartmentID,
                        d.DepartmentName
                    })
                    .ToListAsync();
                ViewBag.Departments = new SelectList(depts, "DepartmentID", "DepartmentName", departmentId);
            }

            return View(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Expenses page failed for user {UserID}, role {RoleID}, tenant {TenantID}.",
                    CurrentUserID,
                    roleId,
                    tenantFilter);

                PopulateIndexFallbackViewBags(
                    roleId,
                    departmentId,
                    status,
                    startDate,
                    endDate,
                    search);

                return View(new List<Expense>());
            }
        }

        // ─────────────────────────────────────────────
        // GET: Expenses/Create
        // ─────────────────────────────────────────────
        private void PopulateIndexFallbackViewBags(
            int roleId,
            int? departmentId,
            string? status,
            DateTime? startDate,
            DateTime? endDate,
            string? search)
        {
            ViewBag.TotalExpenses = 0m;
            ViewBag.MonthlyExpenses = 0m;
            ViewBag.RemainingBudget = 0m;
            ViewBag.TotalAllocated = 0m;
            ViewBag.CurrentPage = 1;
            ViewBag.TotalPages = 1;
            ViewBag.TotalCount = 0;
            ViewBag.RoleID = roleId;
            ViewBag.CanManage = CanManage;
            ViewBag.CurrentDepartment = departmentId;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentStartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentEndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentSearch = search;
            ViewBag.Departments = new SelectList(new List<Department>(), "DepartmentID", "DepartmentName", departmentId);
        }

        private async Task<IActionResult> RenderExpenseIndexCompatibilityAsync(
            int roleId,
            int? tenantFilter,
            int? departmentId,
            string? status,
            DateTime? startDate,
            DateTime? endDate,
            string? search,
            int page)
        {
            await EnsureFinanceSchemaBestEffortAsync();

            var scopedDepartmentId = roleId == Roles.DepartmentHead
                ? HttpContext.Session.GetInt32("DepartmentID")
                : departmentId;

            var filteredRows = await LoadExpenseRowsAsync(
                tenantFilter,
                scopedDepartmentId,
                status,
                startDate,
                endDate,
                search);

            var totalExpenses = filteredRows.Sum(e => e.Amount);
            var totalCount = filteredRows.Count;

            var now = DateTime.Now;
            var monthlyExpenses = filteredRows
                .Where(e => e.ExpenseDate.Year == now.Year && e.ExpenseDate.Month == now.Month)
                .Sum(e => e.Amount);

            var budgetIds = filteredRows
                .Select(e => e.BudgetID)
                .Where(id => id > 0)
                .Distinct()
                .ToHashSet();

            var totalAllocated = 0m;
            var totalSpent = 0m;
            if (budgetIds.Count > 0)
            {
                var budgetAmounts = await LoadBudgetAmountsAsync(tenantFilter);
                var expenseTotalsByBudget = await LoadExpenseTotalsByBudgetAsync(tenantFilter);

                totalAllocated = budgetAmounts
                    .Where(b => budgetIds.Contains(b.Key))
                    .Sum(b => b.Value);

                totalSpent = expenseTotalsByBudget
                    .Where(e => budgetIds.Contains(e.Key))
                    .Sum(e => e.Value);
            }

            var remainingBudget = totalAllocated - totalSpent;

            const int pageSize = 15;
            if (page < 1) page = 1;

            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (totalPages == 0) totalPages = 1;
            if (page > totalPages) page = totalPages;

            var items = filteredRows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            ViewBag.TotalExpenses = totalExpenses;
            ViewBag.MonthlyExpenses = monthlyExpenses;
            ViewBag.RemainingBudget = remainingBudget;
            ViewBag.TotalAllocated = totalAllocated;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.TotalCount = totalCount;
            ViewBag.RoleID = roleId;
            ViewBag.CanManage = CanManage;
            ViewBag.CurrentDepartment = departmentId;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentStartDate = startDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentEndDate = endDate?.ToString("yyyy-MM-dd");
            ViewBag.CurrentSearch = search;

            if (roleId != Roles.DepartmentHead)
            {
                var depts = await LoadDepartmentOptionsAsync(tenantFilter);
                ViewBag.Departments = new SelectList(depts, "DepartmentID", "DepartmentName", departmentId);
            }
            else
            {
                ViewBag.Departments = new SelectList(new List<Department>(), "DepartmentID", "DepartmentName");
            }

            return View(items);
        }

        private async Task EnsureFinanceSchemaBestEffortAsync()
        {
            try
            {
                await DbInitializer.EnsureExpenseSchemaAsync(_db, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Finance schema repair failed before loading the expense page; continuing with compatibility queries.");
            }
        }

        private async Task<List<Expense>> LoadExpenseRowsAsync(
            int? tenantFilter,
            int? departmentId,
            string? status,
            DateTime? startDate,
            DateTime? endDate,
            string? search)
        {
            var columns = await LoadTableColumnInfoAsync("Expenses");
            var budgetRequestExpression = columns.ContainsKey("BudgetRequestID")
                ? "e.BudgetRequestID"
                : "CAST(NULL AS INT)";
            var descriptionExpression = columns.ContainsKey("Description")
                ? "e.[Description]"
                : "CAST('' AS NVARCHAR(1000))";
            var titleExpression = columns.ContainsKey("ExpenseTitle")
                ? $"COALESCE(NULLIF(e.ExpenseTitle, ''), NULLIF({descriptionExpression}, ''), 'Expense')"
                : $"COALESCE(NULLIF({descriptionExpression}, ''), 'Expense')";
            var categoryExpression = columns.ContainsKey("Category")
                ? "COALESCE(NULLIF(e.Category, ''), NULLIF(b.Category, ''), 'General')"
                : "COALESCE(NULLIF(b.Category, ''), 'General')";
            var dateExpression = GetExpenseDateSqlExpression(columns);
            var statusExpression = columns.ContainsKey("Status")
                ? "COALESCE(NULLIF(e.[Status], ''), 'Recorded')"
                : "'Recorded'";

            var sql = $@"
                SELECT
                    e.ExpenseID,
                    {budgetRequestExpression} AS BudgetRequestID,
                    e.BudgetID,
                    e.DepartmentID,
                    e.TenantID,
                    {titleExpression} AS ExpenseTitle,
                    {categoryExpression} AS Category,
                    {descriptionExpression} AS [Description],
                    e.Amount,
                    {dateExpression} AS ExpenseDate,
                    {statusExpression} AS [Status],
                    d.DepartmentName
                FROM Expenses e
                LEFT JOIN Departments d ON d.DepartmentID = e.DepartmentID
                LEFT JOIN Budgets b ON b.BudgetID = e.BudgetID
                WHERE (@TenantID IS NULL OR e.TenantID = @TenantID)
                  AND (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID)
                  AND (@Status IS NULL OR {statusExpression} = @Status)
                  AND (@StartDate IS NULL OR {dateExpression} >= @StartDate)
                  AND (@EndDate IS NULL OR {dateExpression} < @EndDate)
                  AND (
                        @Search IS NULL
                        OR {titleExpression} LIKE @Search
                        OR {categoryExpression} LIKE @Search
                        OR {descriptionExpression} LIKE @Search
                  )
                ORDER BY {dateExpression} DESC, e.ExpenseID DESC";

            var searchTerm = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";

            return await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TenantID", tenantFilter);
                AddParameter(command, "@DepartmentID", departmentId.HasValue && departmentId.Value > 0 ? departmentId.Value : null);
                AddParameter(command, "@Status", string.IsNullOrWhiteSpace(status) ? null : status.Trim());
                AddParameter(command, "@StartDate", startDate?.Date);
                AddParameter(command, "@EndDate", endDate?.Date.AddDays(1));
                AddParameter(command, "@Search", searchTerm);
            }, reader =>
            {
                var departmentName = GetStringOrNull(reader, "DepartmentName");
                var departmentKey = GetInt32(reader, "DepartmentID");
                var expenseDate = GetDateTime(reader, "ExpenseDate", DateTime.Now);

                return new Expense
                {
                    ExpenseID = GetInt32(reader, "ExpenseID"),
                    BudgetRequestID = GetNullableInt32(reader, "BudgetRequestID"),
                    BudgetID = GetInt32(reader, "BudgetID"),
                    DepartmentID = departmentKey,
                    TenantID = GetInt32(reader, "TenantID"),
                    ExpenseTitle = GetString(reader, "ExpenseTitle", "Expense"),
                    Category = GetString(reader, "Category", "General"),
                    Description = GetString(reader, "Description", string.Empty),
                    Amount = GetDecimal(reader, "Amount"),
                    ExpenseDate = expenseDate,
                    Year = expenseDate.Year,
                    Status = GetString(reader, "Status", "Recorded"),
                    CreatedAt = expenseDate,
                    Department = string.IsNullOrWhiteSpace(departmentName)
                        ? null
                        : new Department
                        {
                            DepartmentID = departmentKey,
                            DepartmentName = departmentName
                        }
                };
            });
        }

        private async Task<Dictionary<int, decimal>> LoadBudgetAmountsAsync(int? tenantFilter)
        {
            const string sql = @"
                SELECT BudgetID, Amount
                FROM Budgets
                WHERE (@TenantID IS NULL OR TenantID = @TenantID)";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TenantID", tenantFilter);
            }, reader => new KeyValuePair<int, decimal>(
                GetInt32(reader, "BudgetID"),
                GetDecimal(reader, "Amount")));

            return rows
                .GroupBy(row => row.Key)
                .ToDictionary(group => group.Key, group => group.First().Value);
        }

        private async Task<Dictionary<int, decimal>> LoadExpenseTotalsByBudgetAsync(int? tenantFilter)
        {
            const string sql = @"
                SELECT BudgetID, SUM(Amount) AS TotalAmount
                FROM Expenses
                WHERE BudgetID > 0
                  AND (@TenantID IS NULL OR TenantID = @TenantID)
                GROUP BY BudgetID";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TenantID", tenantFilter);
            }, reader => new KeyValuePair<int, decimal>(
                GetInt32(reader, "BudgetID"),
                GetDecimal(reader, "TotalAmount")));

            return rows
                .GroupBy(row => row.Key)
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Value));
        }

        private async Task<List<Department>> LoadDepartmentOptionsAsync(int? tenantFilter)
        {
            const string sql = @"
                SELECT DepartmentID, DepartmentName, TenantID
                FROM Departments
                WHERE (@TenantID IS NULL OR TenantID = @TenantID)
                ORDER BY DepartmentName";

            return await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TenantID", tenantFilter);
            }, reader => new Department
            {
                DepartmentID = GetInt32(reader, "DepartmentID"),
                DepartmentName = GetString(reader, "DepartmentName", "General"),
                TenantID = GetInt32(reader, "TenantID")
            });
        }

        private async Task<Dictionary<string, TableColumnInfo>> LoadTableColumnInfoAsync(string tableName)
        {
            const string sql = @"
                SELECT
                    c.name AS ColumnName,
                    CASE
                        WHEN t.name IN ('nvarchar', 'nchar') AND c.max_length > 0 THEN c.max_length / 2
                        ELSE c.max_length
                    END AS MaxLength
                FROM sys.columns c
                INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(@TableName)";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TableName", tableName);
            }, reader => new TableColumnInfo
            {
                Name = GetString(reader, "ColumnName", string.Empty),
                MaxLength = GetInt32(reader, "MaxLength")
            });

            var result = new Dictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            {
                result[row.Name] = row;
            }

            return result;
        }

        private static string GetExpenseDateSqlExpression(IReadOnlyDictionary<string, TableColumnInfo> columns)
        {
            if (columns.ContainsKey("ExpenseDate"))
                return "e.ExpenseDate";

            if (columns.ContainsKey("Date"))
                return "e.[Date]";

            if (columns.ContainsKey("CreatedAt"))
                return "e.CreatedAt";

            return "GETDATE()";
        }

        private static string EscapeSqlIdentifier(string identifier)
        {
            return $"[{identifier.Replace("]", "]]")}]";
        }

        private static string TrimForColumn(
            string? value,
            IReadOnlyDictionary<string, TableColumnInfo> columns,
            string columnName,
            string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (columns.TryGetValue(columnName, out var column) && column.MaxLength > 0 && text.Length > column.MaxLength)
                return text[..column.MaxLength];

            return text;
        }

        private async Task<List<T>> ExpenseQueryAsync<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
        {
            var results = new List<T>();
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
                await connection.OpenAsync();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                configure(command);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(map(reader));
                }
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }

            return results;
        }

        private async Task<int> ExpenseExecuteAsync(string sql, Action<DbCommand> configure)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;

            if (shouldClose)
                await connection.OpenAsync();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                configure(command);
                return await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }
        }

        private static void AddParameter(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static int GetInt32(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static int? GetNullableInt32(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static decimal GetDecimal(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));
        }

        private static DateTime GetDateTime(DbDataReader reader, string columnName, DateTime defaultValue)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? defaultValue : Convert.ToDateTime(reader.GetValue(ordinal));
        }

        private static string GetString(DbDataReader reader, string columnName, string defaultValue)
        {
            return GetStringOrNull(reader, columnName) ?? defaultValue;
        }

        private static string? GetStringOrNull(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));
        }

        public async Task<IActionResult> Create()
        {
            if (!IsAuthenticated) return RedirectToLogin();
            if (!CanManage) return AccessDenied();

            await EnsureExpenseCreateSchemaBestEffortAsync();
            await PopulateBudgetDropdown(null);
            return View();
        }

        // ─────────────────────────────────────────────
        // POST: Expenses/Create
        // ─────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Expense model)
        {
            if (!IsAuthenticated) return RedirectToLogin();
            if (!CanManage) return AccessDenied();

            var tenantFilter = GetTenantFilter();

            if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseCreate"))
            {
                try
                {
                    return await CreateExpenseCompatibilityAsync(model, tenantFilter);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Expense create failed for user {UserID}, role {RoleID}, tenant {TenantID}, budget {BudgetID}.",
                        CurrentUserID,
                        CurrentRoleID,
                        tenantFilter,
                        model.BudgetID);

                    return await ExpenseCreateFailureResultAsync(
                        model,
                        "Unable to record the expense because the hosted database rejected the save. Please try again after the latest deployment finishes.");
                }
            }

            // Clear validation state for system-assigned properties not in the form
            ModelState.Remove("DepartmentID");
            ModelState.Remove("TenantID");
            ModelState.Remove("Year");
            ModelState.Remove("CreatedBy");
            ModelState.Remove("Status");

            if (ModelState.IsValid)
            {
                var budget = await _db.Budgets
                    .Include(b => b.Department)
                    .FirstOrDefaultAsync(b =>
                        b.BudgetID == model.BudgetID &&
                        (tenantFilter == null || b.TenantID == tenantFilter.Value) &&
                        b.Status == "Active");

                if (budget == null)
                {
                    ModelState.AddModelError("BudgetID", "Selected approved budget allocation does not exist.");
                }
                else
                {
                    var linkedRequest = await ValidateLinkedRequestAsync(model.BudgetRequestID, budget.BudgetID, budget.TenantID);
                    if (model.BudgetRequestID.HasValue && linkedRequest == null)
                    {
                        ModelState.AddModelError("BudgetRequestID", "Selected budget request is not approved for this allocation.");
                    }

                    ApplyRequestDefaults(model, linkedRequest, budget);

                    await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                    var remaining = await GetRemainingBudgetAsync(budget.BudgetID);

                    if (model.Amount > remaining || remaining < 0)
                    {
                        ModelState.AddModelError("Amount", "Expense amount exceeds the remaining allocated budget.");
                        ModelState.AddModelError("Amount",
                            $"Expense amount (₱{model.Amount:N2}) exceeds remaining budget (₱{remaining:N2}).");

                        // Security log for budget overrun attempt
                        _db.AuditLogs.Add(new AuditLog
                        {
                            UserID = CurrentUserID,
                            TenantID = tenantFilter ?? budget.TenantID,
                            LogType = "Security",
                            Severity = "Warning",
                            Action = "Budget Overrun Attempt",
                            Details = $"User '{CurrentFullName}' attempted expense of ₱{model.Amount:N2} on budget '{budget.Category}' (ID:{budget.BudgetID}). Remaining: ₱{remaining:N2}.",
                            IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                        });
                        await _db.SaveChangesAsync();
                        await tx.CommitAsync();
                    }
                    else if (ModelState.IsValid)
                    {
                        model.TenantID = budget.TenantID;
                        model.DepartmentID = budget.DepartmentID;
                        model.CreatedBy = CurrentUserID ?? 0;
                        model.CreatedAt = DateTime.Now;
                        model.Year = budget.Year;
                        model.Status = "Recorded";

                        _db.Expenses.Add(model);

                        // Audit Log
                        _db.AuditLogs.Add(new AuditLog
                        {
                            UserID = CurrentUserID,
                            TenantID = tenantFilter ?? budget.TenantID,
                            Action = "Expense Created",
                            Details = $"Recorded expense '{model.ExpenseTitle}' for ₱{model.Amount:N2} against budget '{budget.Category}' ({budget.Department?.DepartmentName}).",
                            IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                        });

                        // Notification
                        _db.Notifications.Add(new Notification
                        {
                            TenantID = budget.TenantID,
                            Title = "New Expense Recorded",
                            Message = $"₱{model.Amount:N2} expense '{model.ExpenseTitle}' recorded against {budget.Department?.DepartmentName} budget.",
                            NotificationType = "System",
                            RedirectUrl = "/Expenses"
                        });

                        await _db.SaveChangesAsync();
                        await tx.CommitAsync();

                        TempData["Success"] = "Expense recorded successfully.";
                        return RedirectToAction(nameof(Index));
                    }
                }
            }

            await PopulateBudgetDropdown(model.BudgetID);
            return View(model);
        }

        // ─────────────────────────────────────────────
        // GET: Expenses/Edit/5
        // ─────────────────────────────────────────────
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsAuthenticated) return RedirectToLogin();
            if (!CanManage) return AccessDenied();

            var tenantFilter = GetTenantFilter();
            var expense = await _db.Expenses
                .Include(e => e.Budget)
                .Include(e => e.Department)
                .FirstOrDefaultAsync(e => e.ExpenseID == id && (tenantFilter == null || e.TenantID == tenantFilter));

            if (expense == null) return NotFound();
            if (expense.Status == "Archived")
            {
                TempData["Error"] = "Archived expenses cannot be edited.";
                return RedirectToAction(nameof(Index));
            }

            await PopulateBudgetDropdown(expense.BudgetID);

            // Load budget details for the info panel
            var spent = await _db.Expenses
                .Where(e => e.BudgetID == expense.BudgetID && e.ExpenseID != expense.ExpenseID)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;
            ViewBag.BudgetTotal = expense.Budget?.Amount ?? 0;
            ViewBag.BudgetUsed = spent;
            ViewBag.BudgetRemaining = (expense.Budget?.Amount ?? 0) - spent;

            return View(expense);
        }

        // ─────────────────────────────────────────────
        // POST: Expenses/Edit/5
        // ─────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Expense model)
        {
            if (!IsAuthenticated) return RedirectToLogin();
            if (!CanManage) return AccessDenied();

            var tenantFilter = GetTenantFilter();
            var existing = await _db.Expenses
                .FirstOrDefaultAsync(e => e.ExpenseID == id && (tenantFilter == null || e.TenantID == tenantFilter));

            if (existing == null) return NotFound();
            if (existing.Status == "Archived")
            {
                TempData["Error"] = "Archived expenses cannot be edited.";
                return RedirectToAction(nameof(Index));
            }

            // Clear validation state for system-assigned properties not in the form
            ModelState.Remove("DepartmentID");
            ModelState.Remove("TenantID");
            ModelState.Remove("Year");
            ModelState.Remove("CreatedBy");
            ModelState.Remove("Status");

            if (ModelState.IsValid)
            {
                var budget = await _db.Budgets
                    .FirstOrDefaultAsync(b =>
                        b.BudgetID == model.BudgetID &&
                        (tenantFilter == null || b.TenantID == tenantFilter.Value) &&
                        b.Status == "Active");
                if (budget == null)
                {
                    ModelState.AddModelError("BudgetID", "Selected approved budget allocation does not exist.");
                }
                else
                {
                    var linkedRequest = await ValidateLinkedRequestAsync(model.BudgetRequestID, budget.BudgetID, budget.TenantID);
                    if (model.BudgetRequestID.HasValue && linkedRequest == null)
                    {
                        ModelState.AddModelError("BudgetRequestID", "Selected budget request is not approved for this allocation.");
                    }

                    ApplyRequestDefaults(model, linkedRequest, budget);

                    await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                    var remaining = await GetRemainingBudgetAsync(budget.BudgetID, id);

                    if (model.Amount > remaining || remaining < 0)
                    {
                        ModelState.AddModelError("Amount", "Expense amount exceeds the remaining allocated budget.");
                        ModelState.AddModelError("Amount",
                            $"Expense amount (₱{model.Amount:N2}) exceeds remaining budget (₱{remaining:N2}).");

                        _db.AuditLogs.Add(new AuditLog
                        {
                            UserID = CurrentUserID,
                            TenantID = tenantFilter ?? existing.TenantID,
                            LogType = "Security",
                            Severity = "Warning",
                            Action = "Budget Overrun Attempt",
                            Details = $"User '{CurrentFullName}' attempted to update expense #{id} to ₱{model.Amount:N2}. Remaining: ₱{remaining:N2}.",
                            IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                        });
                        await _db.SaveChangesAsync();
                        await tx.CommitAsync();
                    }
                    else if (ModelState.IsValid)
                    {
                        var oldAmount = existing.Amount;

                        existing.BudgetID = model.BudgetID;
                        existing.BudgetRequestID = model.BudgetRequestID;
                        existing.ExpenseTitle = model.ExpenseTitle;
                        existing.Category = model.Category;
                        existing.Description = model.Description;
                        existing.Amount = model.Amount;
                        existing.ExpenseDate = model.ExpenseDate;
                        existing.DepartmentID = budget.DepartmentID;
                        existing.Year = budget.Year;

                        _db.AuditLogs.Add(new AuditLog
                        {
                            UserID = CurrentUserID,
                            TenantID = tenantFilter ?? existing.TenantID,
                            Action = "Expense Updated",
                            Details = $"Updated expense '{existing.ExpenseTitle}' (ID:{id}). Amount changed from ₱{oldAmount:N2} to ₱{model.Amount:N2}.",
                            IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                        });

                        _db.Notifications.Add(new Notification
                        {
                            TenantID = existing.TenantID,
                            Title = "Expense Updated",
                            Message = $"Expense '{existing.ExpenseTitle}' updated to ₱{model.Amount:N2}.",
                            NotificationType = "System",
                            RedirectUrl = "/Expenses"
                        });

                        await _db.SaveChangesAsync();
                        await tx.CommitAsync();
                        TempData["Success"] = "Expense updated successfully.";
                        return RedirectToAction(nameof(Index));
                    }
                }
            }

            await PopulateBudgetDropdown(model.BudgetID);
            return View(model);
        }

        // ─────────────────────────────────────────────
        // POST: Expenses/UpdateStatus
        // ─────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus)
        {
            if (!IsAuthenticated) return RedirectToLogin();
            if (!CanManage) return AccessDenied();

            var tenantFilter = GetTenantFilter();
            var expense = await _db.Expenses
                .Include(e => e.Department)
                .FirstOrDefaultAsync(e => e.ExpenseID == id && (tenantFilter == null || e.TenantID == tenantFilter));

            if (expense == null) return NotFound();

            var validStatuses = new[] { "Recorded", "Verified", "Archived" };
            if (!validStatuses.Contains(newStatus))
                return BadRequest("Invalid status.");

            var oldStatus = expense.Status;
            expense.Status = newStatus;

            _db.AuditLogs.Add(new AuditLog
            {
                UserID = CurrentUserID,
                TenantID = tenantFilter ?? expense.TenantID,
                Action = newStatus == "Archived" ? "Expense Archived" : "Expense Updated",
                Details = $"Expense '{expense.ExpenseTitle}' (ID:{id}) status changed from '{oldStatus}' to '{newStatus}'.",
                IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            });

            _db.Notifications.Add(new Notification
            {
                TenantID = expense.TenantID,
                Title = $"Expense {newStatus}",
                Message = $"Expense '{expense.ExpenseTitle}' ({expense.Department?.DepartmentName}) has been {newStatus.ToLower()}.",
                NotificationType = "System",
                RedirectUrl = "/Expenses"
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = $"Expense status updated to {newStatus}.";
            return RedirectToAction(nameof(Index));
        }

        // ─────────────────────────────────────────────
        // AJAX: Get approved requests by budget
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetRequestsByBudget(int budgetId)
        {
            if (!IsAuthenticated) return Unauthorized();

            if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseRequestLookup"))
            {
                await EnsureExpenseCreateSchemaBestEffortAsync();
                var compatibleRequests = await LoadApprovedRequestOptionsAsync(budgetId, GetTenantFilter());
                return Json(compatibleRequests.Select(r => new
                {
                    r.RequestID,
                    r.Title,
                    r.Description,
                    r.RequestedAmount,
                    r.BudgetID,
                    Department = r.DepartmentName,
                    Category = r.Category
                }));
            }

            var tenantFilter = GetTenantFilter();
            var requests = await _db.BudgetRequests
                .Include(r => r.Department)
                .Include(r => r.Budget)
                .Where(r =>
                    r.BudgetID == budgetId &&
                    r.Status == "Approved" &&
                    (tenantFilter == null || r.TenantID == tenantFilter.Value))
                .Select(r => new
                {
                    r.RequestID,
                    r.Title,
                    r.Description,
                    r.RequestedAmount,
                    r.BudgetID,
                    Department = r.Department != null ? r.Department.DepartmentName : "",
                    Category = r.Budget != null ? r.Budget.Category : ""
                })
                .ToListAsync();

            return Json(requests);
        }

        [HttpGet]
        public async Task<IActionResult> GetRequestDetails(int requestId)
        {
            if (!IsAuthenticated) return Unauthorized();

            if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseRequestDetails"))
            {
                await EnsureExpenseCreateSchemaBestEffortAsync();
                var compatibleRequest = await LoadApprovedRequestByIdAsync(requestId, null, GetTenantFilter());
                if (compatibleRequest == null) return NotFound();

                return Json(new
                {
                    compatibleRequest.RequestID,
                    compatibleRequest.Title,
                    compatibleRequest.Description,
                    compatibleRequest.RequestedAmount,
                    compatibleRequest.BudgetID,
                    Department = compatibleRequest.DepartmentName,
                    Category = compatibleRequest.Category
                });
            }

            var tenantFilter = GetTenantFilter();
            var request = await _db.BudgetRequests
                .Include(r => r.Department)
                .Include(r => r.Budget)
                .FirstOrDefaultAsync(r =>
                    r.RequestID == requestId &&
                    r.Status == "Approved" &&
                    (tenantFilter == null || r.TenantID == tenantFilter.Value));

            if (request == null) return NotFound();

            return Json(new
            {
                request.RequestID,
                request.Title,
                request.Description,
                request.RequestedAmount,
                request.BudgetID,
                Department = request.Department?.DepartmentName ?? "",
                Category = request.Budget?.Category ?? ""
            });
        }

        // ─────────────────────────────────────────────
        // AJAX: Get budget details
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetBudgetDetails(int budgetId, int? excludeExpenseId = null)
        {
            if (!IsAuthenticated) return Unauthorized();

            if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseBudgetDetails"))
            {
                await EnsureExpenseCreateSchemaBestEffortAsync();
                var compatibleBudget = await LoadExpenseBudgetForCreateAsync(budgetId, GetTenantFilter());
                if (compatibleBudget == null) return NotFound();

                var compatibleSpent = await GetBudgetSpentCompatibilityAsync(budgetId, excludeExpenseId);
                var compatibleRemaining = compatibleBudget.Amount - compatibleSpent;
                var compatibleUtilization = compatibleBudget.Amount > 0
                    ? Math.Round((compatibleSpent / compatibleBudget.Amount) * 100m, 1)
                    : 0m;
                var compatibleIndicator = "healthy";
                if (compatibleRemaining < 0 || compatibleUtilization >= 90)
                    compatibleIndicator = "danger";
                else if (compatibleUtilization >= 70)
                    compatibleIndicator = "warning";

                return Json(new
                {
                    Total = compatibleBudget.Amount,
                    Used = compatibleSpent,
                    Remaining = compatibleRemaining,
                    Utilization = compatibleUtilization,
                    Indicator = compatibleIndicator,
                    Department = compatibleBudget.DepartmentName,
                    Category = compatibleBudget.Category,
                    Year = compatibleBudget.Year
                });
            }

            var tenantFilter = GetTenantFilter();
            var budget = await _db.Budgets
                .Include(b => b.Department)
                .FirstOrDefaultAsync(b =>
                    b.BudgetID == budgetId &&
                    (tenantFilter == null || b.TenantID == tenantFilter.Value) &&
                    b.Status == "Active");
            if (budget == null) return NotFound();

            var expenseQuery = _db.Expenses.Where(e => e.BudgetID == budgetId);
            if (excludeExpenseId.HasValue)
                expenseQuery = expenseQuery.Where(e => e.ExpenseID != excludeExpenseId.Value);

            var totalExpenses = await expenseQuery.SumAsync(e => (decimal?)e.Amount) ?? 0m;

            var remaining = budget.Amount - totalExpenses;
            var utilization = budget.Amount > 0
                ? Math.Round((totalExpenses / budget.Amount) * 100m, 1)
                : 0m;
            var indicator = "healthy";
            if (remaining < 0 || utilization >= 90)
                indicator = "danger";
            else if (utilization >= 70)
                indicator = "warning";

            return Json(new
            {
                Total = budget.Amount,
                Used = totalExpenses,
                Remaining = remaining,
                Utilization = utilization,
                Indicator = indicator,
                Department = budget.Department?.DepartmentName ?? "N/A",
                Category = budget.Category,
                Year = budget.Year
            });
        }

        // ─────────────────────────────────────────────
        // Helper: Populate budget dropdown
        // ─────────────────────────────────────────────
        private async Task<IActionResult> CreateExpenseCompatibilityAsync(Expense model, int? tenantFilter)
        {
            await EnsureExpenseCreateSchemaBestEffortAsync();
            ClearExpenseCreateModelState();

            var budget = await LoadExpenseBudgetForCreateAsync(model.BudgetID, tenantFilter);
            if (budget == null)
            {
                ModelState.AddModelError(nameof(model.BudgetID), "Selected approved budget allocation does not exist.");
                return await ExpenseCreateFailureResultAsync(model, "Selected approved budget allocation does not exist.");
            }

            var linkedRequest = await LoadApprovedRequestByIdAsync(model.BudgetRequestID, budget.BudgetID, tenantFilter);
            if (model.BudgetRequestID.HasValue && linkedRequest == null)
            {
                ModelState.AddModelError(nameof(model.BudgetRequestID), "Selected budget request is not approved for this allocation.");
                return await ExpenseCreateFailureResultAsync(model, "Selected budget request is not approved for this allocation.");
            }

            ApplyRequestDefaults(model, linkedRequest, budget);
            ClearResolvedExpenseTextErrors(model);

            if (!ModelState.IsValid)
                return await ExpenseCreateFailureResultAsync(model, "Please check the required fields and try again.");

            var spent = await GetBudgetSpentCompatibilityAsync(budget.BudgetID);
            var remaining = budget.Amount - spent;
            if (model.Amount > remaining || remaining < 0)
            {
                ModelState.AddModelError(nameof(model.Amount), $"Expense amount ({model.Amount:N2}) exceeds remaining budget ({remaining:N2}).");
                await InsertExpenseAuditLogBestEffortAsync(
                    tenantFilter ?? budget.TenantID,
                    "Security",
                    "Warning",
                    "Budget Overrun Attempt",
                    $"User '{CurrentFullName}' attempted expense of PHP {model.Amount:N2} on budget '{budget.Category}' (ID:{budget.BudgetID}). Remaining: PHP {remaining:N2}.");

                return await ExpenseCreateFailureResultAsync(model, $"Expense amount exceeds the remaining budget ({remaining:N2}).");
            }

            model.TenantID = budget.TenantID;
            model.DepartmentID = budget.DepartmentID;
            model.CreatedBy = CurrentUserID ?? 0;
            model.CreatedAt = DateTime.Now;
            model.Year = budget.Year;
            model.Status = "Recorded";

            await InsertExpenseCompatibilityAsync(model);

            await InsertExpenseAuditLogBestEffortAsync(
                tenantFilter ?? budget.TenantID,
                "System",
                "Info",
                "Expense Created",
                $"Recorded expense '{model.ExpenseTitle}' for PHP {model.Amount:N2} against budget '{budget.Category}' ({budget.DepartmentName}).");

            await InsertExpenseNotificationBestEffortAsync(
                budget.TenantID,
                "New Expense Recorded",
                $"PHP {model.Amount:N2} expense '{model.ExpenseTitle}' recorded against {budget.DepartmentName} budget.");

            TempData["Success"] = "Expense recorded successfully.";

            if (IsAjaxRequest())
                return Ok(new { redirectUrl = Url.Action(nameof(Index), "Expenses") });

            return RedirectToAction(nameof(Index));
        }

        private void ClearExpenseCreateModelState()
        {
            ModelState.Remove("DepartmentID");
            ModelState.Remove("TenantID");
            ModelState.Remove("Year");
            ModelState.Remove("CreatedBy");
            ModelState.Remove("Status");
        }

        private void ClearResolvedExpenseTextErrors(Expense model)
        {
            if (!string.IsNullOrWhiteSpace(model.ExpenseTitle))
                ModelState.Remove(nameof(model.ExpenseTitle));

            if (!string.IsNullOrWhiteSpace(model.Category))
                ModelState.Remove(nameof(model.Category));

            if (!string.IsNullOrWhiteSpace(model.Description))
                ModelState.Remove(nameof(model.Description));
        }

        private async Task<IActionResult> ExpenseCreateFailureResultAsync(Expense model, string fallbackMessage)
        {
            await PopulateBudgetDropdown(model.BudgetID);

            if (IsAjaxRequest())
            {
                var messages = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? fallbackMessage : e.ErrorMessage)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct()
                    .ToList();

                return BadRequest(new
                {
                    message = messages.Count > 0 ? string.Join(" ", messages) : fallbackMessage
                });
            }

            return View(model);
        }

        private bool IsAjaxRequest()
        {
            return string.Equals(
                Request.Headers["X-Requested-With"].ToString(),
                "XMLHttpRequest",
                StringComparison.OrdinalIgnoreCase);
        }

        private async Task EnsureExpenseCreateSchemaBestEffortAsync()
        {
            await EnsureFinanceSchemaBestEffortAsync();

            try
            {
                await DbInitializer.EnsureAuthSchemaAsync(_db, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auth support schema repair failed before recording an expense; continuing with required expense insert only.");
            }
        }

        private async Task<List<SelectListItem>> LoadBudgetDropdownOptionsAsync(int? tenantFilter)
        {
            const string sql = @"
                SELECT
                    b.BudgetID,
                    b.Category,
                    b.[Year],
                    COALESCE(d.DepartmentName, 'N/A') AS DepartmentName
                FROM Budgets b
                LEFT JOIN Departments d ON d.DepartmentID = b.DepartmentID
                WHERE (@TenantID IS NULL OR b.TenantID = @TenantID)
                  AND b.[Status] = 'Active'
                ORDER BY COALESCE(d.DepartmentName, 'N/A'), b.Category, b.[Year] DESC";

            return await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@TenantID", tenantFilter);
            }, reader => new SelectListItem
            {
                Value = GetInt32(reader, "BudgetID").ToString(),
                Text = $"{GetString(reader, "DepartmentName", "N/A")} - {GetString(reader, "Category", "General")} ({GetInt32(reader, "Year")})"
            });
        }

        private async Task<ExpenseBudgetRow?> LoadExpenseBudgetForCreateAsync(int budgetId, int? tenantFilter)
        {
            const string sql = @"
                SELECT
                    b.BudgetID,
                    b.DepartmentID,
                    b.TenantID,
                    b.Category,
                    b.Amount,
                    b.[Year],
                    COALESCE(d.DepartmentName, 'N/A') AS DepartmentName
                FROM Budgets b
                LEFT JOIN Departments d ON d.DepartmentID = b.DepartmentID
                WHERE b.BudgetID = @BudgetID
                  AND (@TenantID IS NULL OR b.TenantID = @TenantID)
                  AND b.[Status] = 'Active'";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@BudgetID", budgetId);
                AddParameter(command, "@TenantID", tenantFilter);
            }, reader => new ExpenseBudgetRow
            {
                BudgetID = GetInt32(reader, "BudgetID"),
                DepartmentID = GetInt32(reader, "DepartmentID"),
                TenantID = GetInt32(reader, "TenantID"),
                Category = GetString(reader, "Category", "General"),
                Amount = GetDecimal(reader, "Amount"),
                Year = GetInt32(reader, "Year"),
                DepartmentName = GetString(reader, "DepartmentName", "N/A")
            });

            return rows.FirstOrDefault();
        }

        private async Task<List<ExpenseRequestRow>> LoadApprovedRequestOptionsAsync(int budgetId, int? tenantFilter)
        {
            const string sql = @"
                SELECT
                    r.RequestID,
                    r.Title,
                    r.[Description],
                    r.RequestedAmount,
                    r.BudgetID,
                    COALESCE(d.DepartmentName, '') AS DepartmentName,
                    COALESCE(b.Category, '') AS Category
                FROM BudgetRequests r
                LEFT JOIN Departments d ON d.DepartmentID = r.DepartmentID
                LEFT JOIN Budgets b ON b.BudgetID = r.BudgetID
                WHERE r.BudgetID = @BudgetID
                  AND r.[Status] = 'Approved'
                  AND (@TenantID IS NULL OR r.TenantID = @TenantID)
                ORDER BY r.CreatedAt DESC, r.RequestID DESC";

            return await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@BudgetID", budgetId);
                AddParameter(command, "@TenantID", tenantFilter);
            }, MapExpenseRequestRow);
        }

        private async Task<ExpenseRequestRow?> LoadApprovedRequestByIdAsync(int? requestId, int? budgetId, int? tenantFilter)
        {
            if (!requestId.HasValue)
                return null;

            const string sql = @"
                SELECT
                    r.RequestID,
                    r.Title,
                    r.[Description],
                    r.RequestedAmount,
                    r.BudgetID,
                    COALESCE(d.DepartmentName, '') AS DepartmentName,
                    COALESCE(b.Category, '') AS Category
                FROM BudgetRequests r
                LEFT JOIN Departments d ON d.DepartmentID = r.DepartmentID
                LEFT JOIN Budgets b ON b.BudgetID = r.BudgetID
                WHERE r.RequestID = @RequestID
                  AND (@BudgetID IS NULL OR r.BudgetID = @BudgetID)
                  AND r.[Status] = 'Approved'
                  AND (@TenantID IS NULL OR r.TenantID = @TenantID)";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@RequestID", requestId.Value);
                AddParameter(command, "@BudgetID", budgetId);
                AddParameter(command, "@TenantID", tenantFilter);
            }, MapExpenseRequestRow);

            return rows.FirstOrDefault();
        }

        private static ExpenseRequestRow MapExpenseRequestRow(DbDataReader reader)
        {
            return new ExpenseRequestRow
            {
                RequestID = GetInt32(reader, "RequestID"),
                Title = GetString(reader, "Title", "Budget Request"),
                Description = GetStringOrNull(reader, "Description"),
                RequestedAmount = GetDecimal(reader, "RequestedAmount"),
                BudgetID = GetInt32(reader, "BudgetID"),
                DepartmentName = GetString(reader, "DepartmentName", string.Empty),
                Category = GetString(reader, "Category", string.Empty)
            };
        }

        private async Task<decimal> GetBudgetSpentCompatibilityAsync(int budgetId, int? excludeExpenseId = null)
        {
            const string sql = @"
                SELECT COALESCE(SUM(Amount), 0) AS Spent
                FROM Expenses
                WHERE BudgetID = @BudgetID
                  AND (@ExcludeExpenseID IS NULL OR ExpenseID <> @ExcludeExpenseID)";

            var rows = await ExpenseQueryAsync(sql, command =>
            {
                AddParameter(command, "@BudgetID", budgetId);
                AddParameter(command, "@ExcludeExpenseID", excludeExpenseId);
            }, reader => GetDecimal(reader, "Spent"));

            return rows.FirstOrDefault();
        }

        private async Task InsertExpenseCompatibilityAsync(Expense model)
        {
            var columns = await LoadTableColumnInfoAsync("Expenses");
            if (!columns.ContainsKey("BudgetID") || !columns.ContainsKey("DepartmentID") ||
                !columns.ContainsKey("TenantID") || !columns.ContainsKey("Amount"))
            {
                throw new InvalidOperationException("The hosted Expenses table is missing required budget, tenant, or amount columns.");
            }

            var insertColumns = new List<string>();
            var insertValues = new List<string>();
            var parameters = new List<(string Name, object? Value)>();
            var expenseDate = model.ExpenseDate == default ? DateTime.Now : model.ExpenseDate;
            var createdAt = model.CreatedAt == default ? DateTime.Now : model.CreatedAt;

            void AddIfExists(string columnName, string parameterName, object? value)
            {
                if (!columns.ContainsKey(columnName))
                    return;

                insertColumns.Add(EscapeSqlIdentifier(columnName));
                insertValues.Add(parameterName);
                parameters.Add((parameterName, value));
            }

            AddIfExists("BudgetRequestID", "@BudgetRequestID", model.BudgetRequestID);
            AddIfExists("BudgetID", "@BudgetID", model.BudgetID);
            AddIfExists("DepartmentID", "@DepartmentID", model.DepartmentID);
            AddIfExists("TenantID", "@TenantID", model.TenantID);
            AddIfExists("ExpenseTitle", "@ExpenseTitle", TrimForColumn(model.ExpenseTitle, columns, "ExpenseTitle", "Expense"));
            AddIfExists("Category", "@Category", TrimForColumn(model.Category, columns, "Category", "General"));
            AddIfExists(
                "Description",
                "@Description",
                TrimForColumn(
                    model.Description,
                    columns,
                    "Description",
                    string.IsNullOrWhiteSpace(model.ExpenseTitle) ? "Expense" : model.ExpenseTitle));
            AddIfExists("Amount", "@Amount", model.Amount);

            if (columns.ContainsKey("ExpenseDate"))
                AddIfExists("ExpenseDate", "@ExpenseDate", expenseDate);
            else
                AddIfExists("Date", "@ExpenseDate", expenseDate);

            AddIfExists("Status", "@Status", TrimForColumn(model.Status, columns, "Status", "Recorded"));
            AddIfExists("CreatedBy", "@CreatedBy", model.CreatedBy);
            AddIfExists("Year", "@Year", model.Year == 0 ? expenseDate.Year : model.Year);
            AddIfExists("CreatedAt", "@CreatedAt", createdAt);

            var sql = $@"
                INSERT INTO Expenses ({string.Join(", ", insertColumns)})
                VALUES ({string.Join(", ", insertValues)})";

            await ExpenseExecuteAsync(sql, command =>
            {
                foreach (var parameter in parameters)
                {
                    AddParameter(command, parameter.Name, parameter.Value);
                }
            });
        }

        private async Task InsertExpenseAuditLogBestEffortAsync(int tenantId, string logType, string severity, string action, string details)
        {
            try
            {
                const string sql = @"
                    INSERT INTO AuditLogs (TenantID, UserID, LogType, Severity, [Action], Details, IPAddress, CreatedAt)
                    VALUES (@TenantID, @UserID, @LogType, @Severity, @Action, @Details, @IPAddress, @CreatedAt)";

                await ExpenseExecuteAsync(sql, command =>
                {
                    AddParameter(command, "@TenantID", tenantId);
                    AddParameter(command, "@UserID", CurrentUserID);
                    AddParameter(command, "@LogType", logType);
                    AddParameter(command, "@Severity", severity);
                    AddParameter(command, "@Action", action);
                    AddParameter(command, "@Details", details);
                    AddParameter(command, "@IPAddress", HttpContext.Connection.RemoteIpAddress?.ToString());
                    AddParameter(command, "@CreatedAt", DateTime.Now);
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Expense audit log insert failed after expense operation.");
            }
        }

        private async Task InsertExpenseNotificationBestEffortAsync(int tenantId, string title, string message)
        {
            try
            {
                const string sql = @"
                    INSERT INTO Notifications (TenantID, UserID, Title, [Message], NotificationType, IsRead, RedirectUrl, CreatedAt)
                    VALUES (@TenantID, @UserID, @Title, @Message, @NotificationType, @IsRead, @RedirectUrl, @CreatedAt)";

                await ExpenseExecuteAsync(sql, command =>
                {
                    AddParameter(command, "@TenantID", tenantId);
                    AddParameter(command, "@UserID", null);
                    AddParameter(command, "@Title", title);
                    AddParameter(command, "@Message", message);
                    AddParameter(command, "@NotificationType", "System");
                    AddParameter(command, "@IsRead", false);
                    AddParameter(command, "@RedirectUrl", "/Expenses");
                    AddParameter(command, "@CreatedAt", DateTime.Now);
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Expense notification insert failed after expense creation.");
            }
        }

        private static void ApplyRequestDefaults(Expense model, ExpenseRequestRow? request, ExpenseBudgetRow budget)
        {
            if (request == null) return;

            if (string.IsNullOrWhiteSpace(model.ExpenseTitle))
                model.ExpenseTitle = request.Title;

            if (string.IsNullOrWhiteSpace(model.Category))
                model.Category = string.IsNullOrWhiteSpace(request.Category) ? budget.Category : request.Category;

            if (string.IsNullOrWhiteSpace(model.Description) && !string.IsNullOrWhiteSpace(request.Description))
                model.Description = request.Description;
        }

        private static void ApplyRequestDefaults(Expense model, BudgetRequest? request, Budget budget)
        {
            if (request == null) return;

            if (string.IsNullOrWhiteSpace(model.ExpenseTitle))
                model.ExpenseTitle = request.Title;

            if (string.IsNullOrWhiteSpace(model.Category))
                model.Category = budget.Category;

            if (string.IsNullOrWhiteSpace(model.Description) && !string.IsNullOrWhiteSpace(request.Description))
                model.Description = request.Description;
        }

        private async Task PopulateBudgetDropdown(int? selectedBudgetId)
        {
            if (!HttpContext.Items.ContainsKey("__UseLegacyExpenseBudgetDropdown"))
            {
                var budgetOptions = await LoadBudgetDropdownOptionsAsync(GetTenantFilter());
                ViewBag.BudgetID = new SelectList(budgetOptions, "Value", "Text", selectedBudgetId);
                return;
            }

            var tenantFilter = GetTenantFilter();
            var budgets = await _db.Budgets
                .Include(b => b.Department)
                .Where(b => (tenantFilter == null || b.TenantID == tenantFilter) && b.Status == "Active")
                .Select(b => new
                {
                    b.BudgetID,
                    DisplayText = b.Department!.DepartmentName + " - " + b.Category + " (" + b.Year + ")"
                })
                .ToListAsync();

            ViewBag.BudgetID = new SelectList(budgets, "BudgetID", "DisplayText", selectedBudgetId);
        }

        private async Task<decimal> GetRemainingBudgetAsync(int budgetId, int? excludeExpenseId = null)
        {
            var budgetAmount = await _db.Budgets
                .Where(b => b.BudgetID == budgetId)
                .Select(b => b.Amount)
                .FirstAsync();

            var expenseQuery = _db.Expenses.Where(e => e.BudgetID == budgetId);
            if (excludeExpenseId.HasValue)
                expenseQuery = expenseQuery.Where(e => e.ExpenseID != excludeExpenseId.Value);

            var spent = await expenseQuery.SumAsync(e => (decimal?)e.Amount) ?? 0m;
            return budgetAmount - spent;
        }

        private async Task<BudgetRequest?> ValidateLinkedRequestAsync(int? requestId, int budgetId, int tenantId)
        {
            if (!requestId.HasValue) return null;

            return await _db.BudgetRequests
                .Include(r => r.Budget)
                .FirstOrDefaultAsync(r =>
                    r.RequestID == requestId.Value &&
                    r.BudgetID == budgetId &&
                    r.TenantID == tenantId &&
                    r.Status == "Approved");
        }

        private sealed class ExpenseBudgetRow
        {
            public int BudgetID { get; set; }
            public int DepartmentID { get; set; }
            public int TenantID { get; set; }
            public string Category { get; set; } = string.Empty;
            public decimal Amount { get; set; }
            public int Year { get; set; }
            public string DepartmentName { get; set; } = "N/A";
        }

        private sealed class ExpenseRequestRow
        {
            public int RequestID { get; set; }
            public string Title { get; set; } = "Budget Request";
            public string? Description { get; set; }
            public decimal RequestedAmount { get; set; }
            public int BudgetID { get; set; }
            public string DepartmentName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
        }

        private sealed class TableColumnInfo
        {
            public string Name { get; set; } = string.Empty;
            public int MaxLength { get; set; }
        }

    }
}
