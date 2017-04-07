using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Baseline;
using FastExpressionCompiler;
using Marten.Schema.Arguments;
using Marten.Schema.Identity;
using Marten.Services;
using Marten.Util;
using Npgsql;

namespace Marten.Schema.BulkLoading
{
    public class BulkLoader<T> : IBulkLoader<T>
    {
        private readonly IdAssignment<T> _assignment;
        private readonly string _baseSql;
        private readonly DocumentMapping _mapping;
        private readonly string _sql;

        private readonly Action<T, string, ISerializer, NpgsqlBinaryImporter, CharArrayTextWriter> _transferData;
        private readonly string _tempTableName;


        public BulkLoader(ISerializer serializer, DocumentMapping mapping, IdAssignment<T> assignment, bool useCharBufferPooling)
        {
            _mapping = mapping;
            _assignment = assignment;
            var upsertFunction = new UpsertFunction(mapping);

            _tempTableName = mapping.Table.Name + "_temp";


            var writer = Expression.Parameter(typeof(NpgsqlBinaryImporter), "writer");
            var document = Expression.Parameter(typeof(T), "document");
            var alias = Expression.Parameter(typeof(string), "alias");
            var serializerParam = Expression.Parameter(typeof(ISerializer), "serializer");
            var textWriter = Expression.Parameter(typeof(CharArrayTextWriter), "writer");

            var arguments = upsertFunction.OrderedArguments().Where(x => !(x is CurrentVersionArgument)).ToArray();
            var expressions =
                arguments.Select(
                    x => x.CompileBulkImporter(serializer.EnumStorage, writer, document, alias, serializerParam, textWriter, useCharBufferPooling));

            var columns = arguments.Select(x => $"\"{x.Column}\"").Join(", ");
            _baseSql = $"COPY %TABLE%({columns}) FROM STDIN BINARY";
            _sql = _baseSql.Replace("%TABLE%", mapping.Table.QualifiedName);

            var block = Expression.Block(expressions);

            var lambda = Expression.Lambda<Action<T, string, ISerializer, NpgsqlBinaryImporter, CharArrayTextWriter>>(block, document, alias,
                serializerParam, writer, textWriter);

            _transferData = ExpressionCompiler.Compile<Action<T, string, ISerializer, NpgsqlBinaryImporter, CharArrayTextWriter>>(lambda);
        }

        public void Load(ISerializer serializer, NpgsqlConnection conn, IEnumerable<T> documents, CharArrayTextWriter textWriter)
        {
            load(serializer, conn, documents, _sql, textWriter);
        }

        public void Load(TableName table, ISerializer serializer, NpgsqlConnection conn, IEnumerable<T> documents, CharArrayTextWriter textWriter)
        {
            var sql = _baseSql.Replace("%TABLE%", table.QualifiedName);
            load(serializer, conn, documents, sql, textWriter);
        }

        public void LoadIntoTempTable(ISerializer serializer, NpgsqlConnection conn, IEnumerable<T> documents, CharArrayTextWriter textWriter)
        {
            var sql = _baseSql.Replace("%TABLE%", _tempTableName);
            load(serializer, conn, documents, sql, textWriter);
        }

        public string CopyNewDocumentsFromTempTable()
        {
            var table = _mapping.SchemaObjects.StorageTable();

            var storageTable = table.Table.QualifiedName;
            var columns = table.Columns.Where(x => x.Name != DocumentMapping.LastModifiedColumn).Select(x => $"\"{x.Name}\"").Join(", ");
            var selectColumns = table.Columns.Where(x => x.Name != DocumentMapping.LastModifiedColumn).Select(x => $"{_tempTableName}.\"{x.Name}\"").Join(", ");

            return $@"insert into {storageTable} ({columns}, {DocumentMapping.LastModifiedColumn}) (select {selectColumns}, transaction_timestamp() from {_tempTableName} 
                         left join {storageTable} on {_tempTableName}.id = {storageTable}.id where {storageTable}.id is null)";
        }

        public string OverwriteDuplicatesFromTempTable()
        {
            var table = _mapping.SchemaObjects.StorageTable();
            var storageTable = table.Table.QualifiedName;

            var updates = table.Columns.Where(x => x.Name != "id" && x.Name != DocumentMapping.LastModifiedColumn)
                .Select(x => $"{x.Name} = source.{x.Name}").Join(", ");

            return $@"update {storageTable} target SET {updates}, {DocumentMapping.LastModifiedColumn} = transaction_timestamp() FROM {_tempTableName} source WHERE source.id = target.id";
        }

        public string CreateTempTableForCopying()
        {
            var tempTable = StorageTable.Name + "_temp";

            return $"create temporary table {tempTable} as select * from {StorageTable.QualifiedName};truncate table {_tempTableName}";
        }

        public TableName StorageTable => _mapping.Table;

        private void load(ISerializer serializer, NpgsqlConnection conn, IEnumerable<T> documents, string sql, CharArrayTextWriter textWriter)
        {
            using (var writer = conn.BeginBinaryImport(sql))
            {
                foreach (var document in documents)
                {
                    var assigned = false;
                    _assignment.Assign(document, out assigned);

                    writer.StartRow();

                    _transferData(document, _mapping.AliasFor(document.GetType()), serializer, writer, textWriter);
                    textWriter.Clear();
                }
            }
        }
    }
}
