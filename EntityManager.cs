﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using DBApi.Events;
using DBApi.Exceptions;
using DBApi.QueryBuilder;
using DBApi.Reflection;

namespace DBApi
{
    public sealed class EntityManager : IEntityManager
    {
        #region Entity Manager Events
        /// <summary>
        /// Triggered when an enumeration begins
        /// </summary>
        public event EventHandler<EntityEnumerationEventArgs> BeginListing;
        /// <summary>
        /// Triggered when an entity is loaded
        /// </summary>
        public event EventHandler<EntityLoadedEventArgs> EntityLoaded;
        /// <summary>
        /// Triggered when an enumeration completes successfully
        /// </summary>
        public event EventHandler<EntityEnumerationEventArgs> EndListing;

        /// <summary>
        /// Invokes a <see cref="BeginListing"/> event
        /// </summary>
        /// <param name="entityType">Type of the entity</param>
        /// <param name="count">Number of entities to be loaded</param>
        /// <remarks>It is wise to pass the <see cref="count"/> parameter, in order both to verify the operation
        /// and display the enumeration's progress</remarks>
        private void OnBeginListing(Type entityType, long count)
        {
            BeginListing?.Invoke(this, new EntityEnumerationEventArgs(count, entityType));
        }
        /// <summary>
        /// Invokes an <see cref="EntityLoaded"/> event
        /// </summary>
        /// <param name="entityType">The type of the entity</param>
        /// <param name="identifier">Optionally the identifier of the entity</param>
        private void OnEntityLoaded(Type entityType, object identifier = null)
        {
            EntityLoaded?.Invoke(this, new EntityLoadedEventArgs(entityType, identifier));
        }
        /// <summary>
        /// Invokes an <see cref="EndListing"/> event
        /// </summary>
        /// <param name="entityType">The type of the entity that was enumerated</param>
        /// <param name="count">A count of the entities in the collection</param>
        private void OnEndListing(Type entityType, long count)
        {
            EndListing?.Invoke(this, new EntityEnumerationEventArgs(count, entityType));
        }
        #endregion

        private readonly string _connectionString;

        /// <summary>
        /// Creates a new Entity manager
        /// </summary>
        /// <param name="connectionString">A Valid SQL Server Connection String</param>
        /// <exception cref="ArgumentNullException">Thrown when a null connection string is passed to <see cref="connectionString"/></exception>
        public EntityManager(string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            
            _connectionString = connectionString;
        }

        /// <summary>
        ///     Number of times the EntityManager can retry the transaction before throwing an exception
        /// </summary>
        // ReSharper disable once MemberCanBePrivate.Global
        public int MaxRetries { get; set; } = 3;

        public T Persist<T>(T entityObject) where T : class
        {
            return Persist(typeof(T), entityObject) as T;
        }

        public T Update<T>(T entityObject) where T : class
        {
            return Update(typeof(T), entityObject) as T;
        }

        public T FindById<T>(object identifier) where T : class
        {
            return FindById(typeof(T), identifier) as T;
        }

        public T FindOneBy<T>(Dictionary<string, object> parameters) where T : class
        {
            return FindBy<T>(parameters).FirstOrDefault();
        }

        public object FindOneBy(Type entityType, Dictionary<string, object> parameters)
        {
            return FindBy(entityType, parameters).FirstOrDefault();
        }

        public void Delete<T>(T entityObject) where T : class
        {
            throw new NotImplementedException();
        }

        public void Delete(Type entityType, object entityObject)
        {
            throw new NotImplementedException();
        }

        public object Persist(Type entityType, object entityObject)
        {
            return Persist(entityType, entityObject, 0);
        }

        public object Update(Type entityType, object entityObject)
        {
            return Update(entityType, entityObject, 0);
        }

        public object FindById(Type entityType, object identifier)
        {
            return FindById(entityType, identifier, 0);
        }
        
        public event EventHandler<OperationEventArgs> OperationComplete;

        private void OnOperationComplete(OperationEventArgs args)
        {
            OperationComplete?.Invoke(this, args);
        }

        private void OnOperationComplete(string OperationName, bool IsSuccess = true, long ElapsedMillis = 0)
        {
            OnOperationComplete(new OperationEventArgs(OperationName, IsSuccess, ElapsedMillis));
        }

        public SqlConnection CreateSqlConnection()
        {
            return new SqlConnection(_connectionString);
        }

        public QueryBuilder.QueryBuilder CreateQueryBuilder()
        {
            return new QueryBuilder.QueryBuilder();
        }

        private ClassMetadata GetClassMetadata(Type entityType)
        {
            return MetadataCache.Get(entityType);
        }

        private int GetLastInsertId(SqlConnection connection, SqlTransaction transaction = null)
        {
            if (connection == null || connection.State != ConnectionState.Open)
                throw new Exception("GetLastInsertId Requires a valid and open connection");
            try
            {
                using (var stmt = new Statement("SELECT CONVERT(int, @@IDENTITY)", connection))
                {
                    if (transaction != null)
                        stmt.SetTransaction(transaction);

                    return (int) stmt.FetchScalar();
                }
            }
            catch (SqlException)
            {
                return -1;
            }
        }

        private int FastCount(string tableName, Dictionary<string, object> parameters, int CurrentRetries = 0)
        {
            var queryBuilder = CreateQueryBuilder()
                .Select("COUNT(*)")
                .From(tableName);
            var paramNum = 0;
            if (parameters != null && parameters.Count > 0)
                foreach (var parameter in parameters)
                {
                    if (paramNum == 0)
                        queryBuilder.Where(new Eq($"t.{parameter.Key}", $"@{parameter.Key}"));
                    else
                        queryBuilder.AndWhere(new Eq($"t.{parameter.Key}", $"@{parameter.Key}"));

                    paramNum++;
                }

            var query = queryBuilder.GetQuery();
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query, connection))
                    {
                        stmt.BindParameters(parameters);
                        return (int) stmt.FetchScalar();
                    }
                }
                catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (CurrentRetries < MaxRetries)
                        return FastCount(tableName, parameters, +CurrentRetries);
                    throw;
                }
            }
        }

        private int FastCountStar(ClassMetadata metadata, object identifier)
        {
            return FastCount(metadata.TableName, new Dictionary<string, object>
            {
                {metadata.IdentifierColumn, identifier}
            });
        }

        private string GetOperationName(object obj = null, [CallerMemberName] string name = null)
        {
            return $"{name}:{obj}";
        }

        public object Persist(Type entityType, object entityObject, int CurrentRetries = 0)
        {
#if DEBUG
            var sw = new Stopwatch();
#endif
            if (entityObject == null)
                throw new ArgumentNullException(nameof(entityObject));

            var metadata = GetClassMetadata(entityType);
            var identifier = metadata.GetColumnFieldInfo(metadata.IdentifierColumn)
                .GetValue(entityObject);

            if (identifier != null && (int) identifier != -1)
                if (FastCountStar(metadata, identifier) > 0)
                    return Update(entityType, entityObject);
#if DEBUG
            sw.Start();
#endif
            var query = CreateQueryBuilder()
                .Insert(entityType)
                .GetQuery();
            int lastId;
            SqlTransaction sqlTransaction = null;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    sqlTransaction = connection.BeginTransaction();
                    using (var stmt = new Statement(query, connection))
                    {
                        stmt.SetTransaction(sqlTransaction)
                            .BindParameters(ClassMetadata.GetParameterDictionary(entityObject))
                            .Execute();
                    }

                    if (!metadata.HasGuidColumn())
                    {
                        lastId = GetLastInsertId(connection, sqlTransaction);
                    }
                    else
                    {
                        var gQuery = CreateQueryBuilder()
                            .Select(metadata.IdentifierColumn)
                            .From(metadata.TableName)
                            .Where(new Eq($"t.{metadata.GuidColumn}", "@guid"))
                            .GetQuery();

                        using (var stmt = new Statement(gQuery, connection))
                        {
                            lastId = (int) stmt.SetTransaction(sqlTransaction)
                                .BindParameter("@guid",
                                    metadata.GetColumnFieldInfo(metadata.GuidColumn).GetValue(entityObject))
                                .FetchScalar();
                        }
                    }

                    metadata.GetColumnFieldInfo(metadata.IdentifierColumn)
                        .SetValue(entityObject, lastId);

                    if (metadata.HasCustomColumns())
                    {
                        var customColumns = metadata.Columns.Select(c => c.Value)
                            .Where(c => c.IsCustomColumn)
                            .ToList();
                        foreach (var customColumn in customColumns)
                        {
                            var cquery = customColumn.GetCustomColumnQuery();
                            var parm = customColumn.GetCustomColumnParameters(entityObject);
                            using (var stmt = new Statement(cquery, connection))
                            {
                                stmt.SetTransaction(sqlTransaction)
                                    .BindParameters(parm)
                                    .Execute();
                            }
                        }
                    }

                    sqlTransaction.Commit();
                    connection.Close();
                }
                catch (SqlException ex)
                {
                    if (sqlTransaction != null && connection.State == ConnectionState.Open) sqlTransaction.Rollback();
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (CurrentRetries < MaxRetries) return Persist(entityType, entityObject, ++CurrentRetries);

                    throw new Exception(ex.Message, ex);
                    //throw new ORMStatementException(Query, ex.Message);
                }
            }
#if DEBUG
            sw.Stop();
            Debug.WriteLine($"Persisted ${entityObject} to database in ${sw.ElapsedMilliseconds} msec");
#endif
            entityObject = FindById(entityType, lastId);
            return entityObject;
        }

        public object Update(Type entityType, object entityObject, int CurrentRetries = 0)
        {
            if (entityObject == null) throw new ArgumentNullException(nameof(entityObject));

            var metadata = MetadataCache.Get(entityType);
            var query = CreateQueryBuilder()
                .UpdateInternal(metadata)
                .Where(new Eq(metadata.IdentifierColumn, "@identifier"))
                .GetQuery();

            var identifier = metadata.GetIdentifierField().GetValue(entityObject);
            if (identifier == null || (int) identifier == -1)
                throw new ORMException("An object needs an identifier in order to be updated");

            SqlTransaction sqlTransaction = null;
            using (var Connection = CreateSqlConnection())
            {
                try
                {
                    Connection.Open();
                    sqlTransaction = Connection.BeginTransaction();
                    using (var stmt = new Statement(query, Connection))
                    {
                        stmt.SetTransaction(sqlTransaction)
                            .BindParameters(ClassMetadata.GetParameterDictionary(entityObject))
                            .BindParameter("@identifier", identifier)
                            .Execute();
                    }

                    if (metadata.HasCustomColumns())
                    {
                        var customColumns = metadata.Columns.Select(c => c.Value)
                            .Where(c => c.IsCustomColumn)
                            .ToList();
                        foreach (var customColumn in customColumns)
                        {
                            var cquery = customColumn.GetCustomColumnQuery();
                            var parm = customColumn.GetCustomColumnParameters(entityObject);
                            using (var stmt = new Statement(cquery, Connection))
                            {
                                stmt.SetTransaction(sqlTransaction)
                                    .BindParameters(parm)
                                    .Execute();
                            }
                        }
                    }

                    sqlTransaction.Commit();
                }
                catch (SqlException ex)
                {
                    if (sqlTransaction != null && Connection.State == ConnectionState.Open)
                        sqlTransaction.Rollback();

                    if (Connection.State == ConnectionState.Open)
                        Connection.Close();

                    if (CurrentRetries < MaxRetries) return Update(entityType, entityObject, ++CurrentRetries);
                    throw new ORMStatementException(query, ex.Message);
                }

                Connection.Close();
            }

            if (CacheManager.Contains(entityType, identifier)) CacheManager.Remove(entityType, identifier);
            CacheManager.Add(entityType, identifier);
            //TODO: Check if we need to rehydrate
            return entityObject;
        }

        /// <summary>
        /// Searches for an entity by its Identifier
        /// </summary>
        /// <param name="entityType">The <see cref="Type"/> of the Entity</param>
        /// <param name="identifier">The identifier</param>
        /// <param name="CurrentRetries"></param>
        /// <returns>An object containing the entity or null if it's not found</returns>
        public object FindById(Type entityType, object identifier, int CurrentRetries = 0)
        {
            //Kill all Null Identifiers
            if (identifier == null)
                return null;
            if ((int) identifier < 1)
                return null;
            
            var metadata = GetClassMetadata(entityType);
            if (CacheManager.Contains(entityType, identifier))
            {
                OnEntityLoaded(metadata.EntityType, identifier);
                return CacheManager.Get(entityType, identifier);
            }

            
            var query = CreateQueryBuilder()
                .SelectInternal(metadata)
                .FromInternal(metadata)
                .Where(new Eq($"t.{metadata.IdentifierColumn}", "@identifier"))
                .GetQuery();
            
            object entity;
            using (var Connection = CreateSqlConnection())
            {
                try
                {
                    Connection.Open();
                    DataRow row;
                    //IMPORTANT: HydrateObject does not open another connection to SQL, HydrateCustomColumns does though.
                    //In order to preserve resources - and connections - do close the Connection BEFORE hydrating the entity
                    using (var stmt = new Statement(query, Connection))
                    {
                        stmt.BindParameter("@identifier", identifier);
                        row = stmt.FetchRow();
                    }
                    Connection.Close();
                    entity = HydrateObject(row, metadata);
                }
                catch (SqlException)
                {
                    if (Connection.State == ConnectionState.Open)
                        Connection.Close();

                    if (CurrentRetries < MaxRetries)
                        return FindById(entityType, identifier, ++CurrentRetries);
                    throw;
                }
            }
            CacheManager.Add(entityType, entity);
            
            return entity;
        }

        internal object HydrateObject(DataRow row, ClassMetadata metadata)
        {
            var sw = new Stopwatch();
            sw.Start();
            //Εάν η γραμμή είναι null, θα πρέπει να επιστρέψουμε null - δεν υπάρχει συσχέτιση
            if (row == null) return null;

            //Δημιούργησε το νέο object 
            var entityBase = Activator.CreateInstance(metadata.EntityType);
            
            //Hydrate entity should abuse the EntityCache
            var identifierValue = row[metadata.IdentifierColumn];
            if (CacheManager.Contains(metadata.EntityType, identifierValue))
            {
                OnEntityLoaded(metadata.EntityType, identifierValue);
                return CacheManager.Get(metadata.EntityType, identifierValue);
            }

            var columns = metadata.Columns.Select(c => c.Value)
                .Where(c => c.IsCustomColumn == false)
                .ToList();


            foreach (var column in columns)
            {
                object value = null;
                if (!string.IsNullOrEmpty(column.ColumnName) && row.Table.Columns.Contains(column.ColumnName))
                    value = row[column.ColumnName];

                if (value == null || value == DBNull.Value)
                {
                    if (!column.IsRelationship) continue;

                    if (column.RelationshipType == RelationshipType.ManyToOne) continue;
                }

                if (column.IsRelationship && column.RelationshipType == RelationshipType.ManyToOne)
                {
                    //Note to future self:
                    //Ενώ, κανονικά, δένουμε το ManyToOne με το primaryKey, εδώ έχει πάρει και έχει γαμηθεί.
                    //Οπότε δεν μπορούμε να πάμε να ψάξουμε με FindById, αλλά με FindOneBy και το 
                    //field στο οποίο κάνουμε reference
                    var targetObject = FindOneBy(column.TargetEntity, new Dictionary<string, object>
                    {
                        {column.RelationshipReferenceColumn, value}
                    });
                    column.FieldInfo.SetValue(entityBase, targetObject);
                }
                else if (column.IsRelationship && column.RelationshipType == RelationshipType.OneToMany)
                {
                    var objects = FindBy(column.TargetEntity, new Dictionary<string, object>
                    {
                        {column.RelationshipReferenceColumn, metadata.GetIdentifierField().GetValue(entityBase)}
                    });

                    column.FieldInfo.SetValue(entityBase, ConvertList(objects, column.TargetEntity));
                }
                else
                {
                    column.FieldInfo.SetValue(entityBase, value);
                }
            }

            if (metadata.HasCustomColumns()) HydrateCustomColumns(ref entityBase, metadata);
            sw.Stop();
            //OnOperationComplete(GetOperationName($"\t ObjectHydration: "), true, sw.ElapsedTicks);
            OnEntityLoaded(metadata.EntityType, 0);
            return entityBase;
        }

        private object ConvertList(List<object> source, Type targetType)
        {
            var listType = typeof(List<>).MakeGenericType(targetType);
            var typedList = (IList) Activator.CreateInstance(listType);
            foreach (var item in source) typedList.Add(item);
            return typedList;
        }

        private void HydrateCustomColumns(ref object entityBase, ClassMetadata metadata, int CurrentRetries = 0)
        {
#if DEBUG
            var sw = new Stopwatch();
            sw.Start();
#endif

            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var query = CreateQueryBuilder()
                .Select(metadata.CustomReferenceColumn, "CustomFieldId", "CustomFieldValue")
                .From(metadata.CustomTable)
                .Where(new Eq(metadata.CustomReferenceColumn, "@identifier"))
                .GetQuery();

            DataTable table;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query, connection))
                    {
                        table = stmt.BindParameter("@identifier", metadata.GetIdentifierField().GetValue(entityBase))
                            .Fetch();
                    }

                    connection.Close();
                }
                catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (CurrentRetries < MaxRetries)
                        HydrateCustomColumns(ref entityBase, metadata, ++CurrentRetries);

                    throw;
                    //OnOperationComplete(GetOperationName($"\t CustomColumHydration Failure: "), false, sw.ElapsedTicks);
                }
            }

            if (table == null)
                return;

            foreach (DataRow row in table.Rows)
            {
                var value = row["CustomFieldValue"];
                if (value == null || value == DBNull.Value)
                    continue;

                var columnId = (int) row["CustomFieldId"];
                try
                {
                    value = ConvertCustomColumn(metadata.GetCustomColumnMetadata(columnId), value);
                }
                catch (MetadataException)
                {
                    value = null;
                }

                try
                {
                    metadata.GetCustomColumnFieldInfo(columnId).SetValue(entityBase, value);
                }
                catch
                {
                    // ignored
                }
            }
#if DEBUG
            sw.Stop();
#endif
            //OnOperationComplete(GetOperationName($"\tCustomColumnHydration Complete: "), true, sw.ElapsedTicks);
        }

        private bool ConvertStringToBoolean(string value)
        {
            var isInt = int.TryParse(value, out var boolVal);
            if (!isInt)
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return Convert.ToBoolean(boolVal, CultureInfo.InvariantCulture);
        }

        internal object ConvertCustomColumn(ColumnMetadata meta, object value)
        {
            if (value == null)
                return null;

            if (meta.ColumnType == typeof(SqlBoolean))
                return ConvertStringToBoolean(value.ToString());
            if (meta.ColumnType == typeof(SqlByte))
                return Convert.ToByte(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlDateTime))
                return value.ToString().ConvertStringToDatetime();
            if (meta.ColumnType == typeof(SqlDecimal))
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlDouble))
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlInt16))
                return Convert.ToInt16(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlInt32))
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlInt64))
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlSingle))
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (meta.ColumnType == typeof(SqlString)) return Convert.ToString(value, CultureInfo.InvariantCulture);

            return null;
        }

        public List<T> FindBy<T>(Dictionary<string, object> parameters = null, int currentRetries = 0) where T : class
        { 
            if (currentRetries < 0) throw new ArgumentOutOfRangeException(nameof(currentRetries));
            
            var metadata = MetadataCache.Get<T>();
            long count = FastCount(metadata.TableName, parameters);
            
            var query = CreateQueryBuilder()
                .SelectInternal(metadata)
                .FromInternal(metadata);
            
            var currentParam = 0;
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var parameter in parameters)
                {
                    query = currentParam == 0
                        ? query.Where(new Eq(parameter.Key, $"@{parameter.Key}"))
                        : query.AndWhere(new Eq(parameter.Key, $"@{parameter.Key}"));
                    currentParam++;
                }
            }

            DataTable dt;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query.GetQuery(), connection))
                    {
                        dt = stmt.BindParameters(parameters).Fetch();
                    }
                    connection.Close();
                }catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (currentRetries < MaxRetries)
                        return FindBy<T>(parameters, ++currentRetries);
                    throw;
                }
            }
            // Moving this to a safe spot
            OnBeginListing(metadata.EntityType, count);

            if (dt == null || dt.Rows.Count == 0)
            {
                OnEndListing(metadata.EntityType, 0);
                return null;
            }

            var entityList = (from DataRow row in dt.Rows select HydrateObject(row, metadata) as T).ToList();

            OnEndListing(metadata.EntityType, entityList.Count);
            return entityList;
        }

        public List<object> FindBy(Type entityType, Dictionary<string, object> parameters = null, int currentRetries = 0)
        {
            if (currentRetries < 0) throw new ArgumentOutOfRangeException(nameof(currentRetries));
            
            var metadata = MetadataCache.Get(entityType);
            long count = FastCount(metadata.TableName, parameters);
            
            var query = CreateQueryBuilder()
                .SelectInternal(metadata)
                .FromInternal(metadata);
            
            var currentParam = 0;
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var parameter in parameters)
                {
                    query = currentParam == 0
                        ? query.Where(new Eq(parameter.Key, $"@{parameter.Key}"))
                        : query.AndWhere(new Eq(parameter.Key, $"@{parameter.Key}"));
                    currentParam++;
                }
            }

            DataTable dt;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query.GetQuery(), connection))
                    {
                        dt = stmt.BindParameters(parameters).Fetch();
                    }
                    connection.Close();
                }catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (currentRetries < MaxRetries)
                        return FindBy(entityType, parameters,++currentRetries);
                    throw;
                }
            }
            // Moving this to a safe spot
            OnBeginListing(metadata.EntityType, count);
            
            if (dt == null || dt.Rows.Count == 0)
            {
                OnEndListing(metadata.EntityType, 0);
                return null;
            }
            var entityList = (from DataRow row in dt.Rows select HydrateObject(row, metadata)).ToList();
            OnEndListing(metadata.EntityType, entityList.Count);
            return entityList;
        }

        // ReSharper disable once MemberCanBePrivate.Global
        public List<T> FindAll<T>(int currentRetries = 0) where T : class
        {
            return FindBy<T>();
        }
        
        public List<object> FindAll(Type entityType, int currentRetries = 0)
        {
            return FindBy(entityType);
        }

        // ReSharper disable once MemberCanBePrivate.Global
        /// <summary>
        /// Executes a SQL Query and returns a <see cref="DataTable"/> with the results
        /// </summary>
        /// <param name="query">The Query</param>
        /// <param name="parameters">A <see cref="Dictionary{TKey,TValue}"/> of parameters</param>
        /// <param name="currentRetries">The number of already executed retries</param>
        /// <returns>A <see cref="DataTable"/> with the results or null if no results were produced</returns>
        /// <exception cref="ArgumentNullException">Thrown if the query is empty</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the programmer is dumb enough to use a negative number of retries</exception>
        public DataTable GetResult(string query, Dictionary<string, object> parameters = null, int currentRetries = 0)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentNullException(nameof(query));
                
            if (currentRetries <= 0) throw new ArgumentOutOfRangeException(nameof(currentRetries));

            DataTable dataTable;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query, connection))
                    {
                        dataTable = stmt.BindParameters(parameters)
                            .Fetch();
                    }

                    connection.Close();
                }
                catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (currentRetries < MaxRetries)
                        return GetResult(query, parameters, ++currentRetries);
                    throw;
                }
            }
            return dataTable;
        }
        /// <summary>
        /// Executes a SQL Query and returns a <see cref="DataRow"/> with thr result
        /// </summary>
        /// <param name="query"></param>
        /// <param name="parameters"></param>
        /// <param name="currentRetries"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public DataRow GetSingleResult(string query, Dictionary<string, object> parameters = null, int currentRetries = 0)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentNullException(nameof(query));
            if (currentRetries <= 0) throw new ArgumentOutOfRangeException(nameof(currentRetries));

            DataRow dataRow;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query, connection))
                    {
                        dataRow = stmt.BindParameters(parameters)
                            .FetchRow();
                    }

                    connection.Close();
                }
                catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (currentRetries < MaxRetries)
                        return GetSingleResult(query, parameters, ++currentRetries);
                    throw;
                }
            }

            return dataRow;
        }

        /// <summary>
        /// Executes a SQL Query and returns a <see cref="object"/> with the result
        /// </summary>
        /// <param name="query"></param>
        /// <param name="parameters"></param>
        /// <param name="currentRetries"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public object GetSingleScalarResult(string query, Dictionary<string, object> parameters = null, int currentRetries = 0)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentNullException(nameof(query));
            if (currentRetries <= 0) throw new ArgumentOutOfRangeException(nameof(currentRetries));
            
            object value;
            using (var connection = CreateSqlConnection())
            {
                try
                {
                    connection.Open();
                    using (var stmt = new Statement(query, connection))
                    {
                        value = stmt.BindParameters(parameters)
                            .FetchScalar();
                    }

                    connection.Close();
                }
                catch (SqlException)
                {
                    if (connection.State == ConnectionState.Open)
                        connection.Close();

                    if (currentRetries < MaxRetries)
                        return GetSingleScalarResult(query, parameters, ++currentRetries);
                    throw;
                }
            }
            return value;
        }
    }
}