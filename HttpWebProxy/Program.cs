using Serilog;
using Serilog.Sinks.MSSqlServer;
using System;
using Titanium.Web.Proxy;

namespace HttpWebProxy
{
    class Program
    {
        private static readonly ProxyController controller = new ProxyController();
        static void Main(string[] args)
        {
            ColumnOptions columnOptions = new ColumnOptions();
            columnOptions.AdditionalColumns.Add(new SqlColumn("bodySize", System.Data.SqlDbType.BigInt));
            columnOptions.AdditionalColumns.Add(new SqlColumn("host", System.Data.SqlDbType.NVarChar, allowNull:false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("processId", System.Data.SqlDbType.NVarChar, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("protocol", System.Data.SqlDbType.NVarChar, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("statusCode", System.Data.SqlDbType.NVarChar, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("receivedDataCount", System.Data.SqlDbType.BigInt, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("sentDataCount", System.Data.SqlDbType.BigInt, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("url", System.Data.SqlDbType.NVarChar, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("clientConnectionId", System.Data.SqlDbType.NVarChar, allowNull: false));
            columnOptions.AdditionalColumns.Add(new SqlColumn("serverConnectionId", System.Data.SqlDbType.NVarChar, allowNull: false));

            var logger = new LoggerConfiguration().WriteTo.MSSqlServer(connectionString: "Server=localhost;Database=LogDb;Integrated Security=SSPI;",sinkOptions: new MSSqlServerSinkOptions { TableName = "HttpWebProxySessionLog", BatchPeriod = TimeSpan.FromSeconds(160) }, columnOptions: columnOptions).CreateLogger();

            controller.SetLogger(logger);
            // Start proxy controller
            controller.StartProxy();

            Console.WriteLine("Hit any key to exit..");
            Console.WriteLine();
            Console.Read();

            controller.Stop();
        }
    }
}
