using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System.Data;

namespace HttpWebProxy.Utility.Filter
{
    public class BlackListChecker
    {
        private readonly string constr = @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=D:\project\project\C#\prog\Work\HttpWebProxy\HttpWebProxy\HttpWebProxy\Data\HWPDb.mdf;Integrated Security=True";

        private LinkedList<string> blackList = new LinkedList<string>();
        
        public BlackListChecker()
        {
            UpdateBlackList();
        }

        public void UpdateBlackList()
        {
            blackList = getBlackListInfo();
        }

        private LinkedList<string> getBlackListInfo()
        {
            LinkedList<string> temp = new LinkedList<string>();
            
            using (SqlConnection con = new SqlConnection(connectionString: constr))
            {
                using (SqlCommand cmd = new SqlCommand(cmdText: "SELECT desUrl FROM [dbo].[BlackListTable]", con))
                {
                    DataTable dt = new DataTable();

                    using (SqlDataAdapter sda = new SqlDataAdapter(cmd))
                    {
                        con.Open();
                        sda.Fill(dt);
                    }

                    for (int i = 0; i < dt.Rows.Count; i++)
                    {
                        temp.AddFirst(dt.Rows[i][0].ToString());
                    }
                }
            }

            return temp;
        }
        public bool existInBlackList(string phrase)
        {
            bool existed = false;

            foreach (var item in blackList)
            {
                if (phrase.Contains(item))
                {
                    existed = true;
                    break;
                }
            }
            return existed;
        }
    }

}
