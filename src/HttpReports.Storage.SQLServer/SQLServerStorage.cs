﻿using Dapper;
using Dapper.Contrib.Extensions;
using HttpReports.Core.Models;
using HttpReports.Models;
using HttpReports.Monitor;
using HttpReports.Storage.FilterOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks; 

namespace HttpReports.Storage.SQLServer
{  
    public class SQLServerStorage : IHttpReportsStorage
    {
        public SQLServerStorageOptions _options;

        public SQLServerConnectionFactory ConnectionFactory { get; }

        public ILogger<SQLServerStorage> Logger { get; } 

        private readonly AsyncCallbackDeferFlushCollection<IRequestInfo, IRequestDetail> _deferFlushCollection = null;

        public SQLServerStorage(IOptions<SQLServerStorageOptions> options,SQLServerConnectionFactory connectionFactory, ILogger<SQLServerStorage> logger)
        {
            _options = options.Value;

            ConnectionFactory = connectionFactory;
            Logger = logger;

            if (_options.EnableDefer)
            {
                _deferFlushCollection = new AsyncCallbackDeferFlushCollection<IRequestInfo, IRequestDetail>(AddRequestInfoAsync,_options.DeferThreshold, _options.DeferSecond);
            }
        }

        public async Task InitAsync()
        {
            try
            {
                using (var con = ConnectionFactory.GetConnection()) 
                {   
                     
                    if (con.QueryFirstOrDefault<int>($" Select Count(*) from sysobjects where id = object_id('{ConnectionFactory.DataBase}.dbo.RequestInfo') ") == 0)
                    {
                        await con.ExecuteAsync(@"   
                            CREATE TABLE [dbo].[RequestInfo](
	                            [Id] [varchar](50) NOT NULL PRIMARY KEY,
                                [ParentId] [nvarchar](50) NULL,
	                            [Node] [nvarchar](50) NULL,
	                            [Route] [nvarchar](50) NULL,
	                            [Url] [nvarchar](200) NULL,
	                            [Method] [nvarchar](50) NULL,
	                            [Milliseconds] [int] NULL,
	                            [StatusCode] [int] NULL,
	                            [IP] [nvarchar](50) NULL,
                                [Port] [int] NULL,
                                [LocalIP] [nvarchar](50) NULL,
                                [LocalPort] [int] NULL,
	                            [CreateTime] [datetime] NULL)
                    ").ConfigureAwait(false);
                    } 

                    if (con.QueryFirstOrDefault<int>($" Select Count(*) from sysobjects where id = object_id('{ConnectionFactory.DataBase}.dbo.RequestDetail') ") == 0)
                    {
                        await con.ExecuteAsync(@"   
                            CREATE TABLE [dbo].[RequestDetail](
	                            [Id] [varchar](50) NOT NULL PRIMARY KEY,
	                            [RequestId] [varchar](50) NOT NULL,
                                [Scheme] [varchar](10) NULL,
                                [QueryString] [text] NULL,
                                [Header] [text] NULL,
                                [Cookie] [text] NULL,
                                [RequestBody] [text] NULL,
                                [ResponseBody] [text] NULL,
                                [ErrorMessage] [text] NULL,
                                [ErrorStack] [text] NULL,
                                [CreateTime] [datetime] NULL )
                    ").ConfigureAwait(false);
                    } 

                    if (con.QueryFirstOrDefault<int>($"Select Count(*) from sysobjects where id = object_id('{ConnectionFactory.DataBase}.dbo.MonitorJob')") == 0)
                    {
                        await con.ExecuteAsync(@"  
                            CREATE TABLE [dbo].[MonitorJob](
	                            [Id] [varchar](50) NOT NULL PRIMARY KEY,
	                            [Title] [nvarchar](255) NULL,
                                [Description] [nvarchar](255) NULL,
                                [CronLike] [nvarchar](255) NULL, 
                                [Emails] [nvarchar](1000)  NULL,
                                [WebHook] [nvarchar](1000) NULL,
                                [Mobiles] [nvarchar](1000) NULL,
                                [Status] [int] NULL,
                                [Nodes] [nvarchar](255) NULL,
                                [PayLoad] [nvarchar](2000) NULL, 
	                            [CreateTime] [datetime] NULL )
                      ").ConfigureAwait(false);
                    } 
                   
                    if (con.QueryFirstOrDefault<int>($"Select Count(*) from sysobjects where id = object_id('{ConnectionFactory.DataBase}.dbo.SysUser')") == 0)
                    {
                        await con.ExecuteAsync($@"

                           CREATE TABLE [SysUser]( 
	                            [Id] [varchar](50) NOT NULL PRIMARY KEY,
	                            [UserName] [nvarchar](100) NOT NULL, 
	                            [Password] [nvarchar](100) NOT NULL );  

                            Insert Into [SysUser] Values ('{MD5_16(Guid.NewGuid().ToString())}','{Core.Config.BasicConfig.DefaultUserName}','{Core.Config.BasicConfig.DefaultPassword}'); 

                         ").ConfigureAwait(false);

                    } 

                } 
            }
            catch (Exception ex)
            {
                throw new Exception("Database init failed：" + ex.Message,ex);
            } 
        }
         
        private async Task AddRequestInfoAsync(Dictionary<IRequestInfo, IRequestDetail> list, CancellationToken token)
        {
            await LoggingSqlOperation(async connection =>
            { 
                string requestSql = string.Join(",", list.Select(x => x.Key).Select(x => $" ('{x.Id}','{x.ParentId}','{x.Node}','{x.Route}','{x.Url}','{x.Method}',{x.Milliseconds},{x.StatusCode},'{x.IP}',{x.Port},'{x.LocalIP}',{x.LocalPort},'{x.CreateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")}') ")); 
              
                await connection.ExecuteAsync($"Insert into [RequestInfo] ([Id],[ParentId],[Node],[Route],[Url],[Method],[Milliseconds],[StatusCode],[IP],[Port],[LocalIP],[LocalPort],[CreateTime]) VALUES {requestSql}").ConfigureAwait(false);

                string detailSql = string.Join(",", list.Select(x => x.Value).Select(x => $" ('{x.Id}','{x.RequestId}','{x.Scheme}','{x.QueryString}','{x.Header}','{x.Cookie}','{x.RequestBody}','{x.ResponseBody}','{x.ErrorMessage}','{x.ErrorStack}','{x.CreateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")}' ) "));

                await connection.ExecuteAsync($"Insert into [RequestDetail] (Id,RequestId,Scheme,QueryString,Header,Cookie,RequestBody,ResponseBody,ErrorMessage,ErrorStack,CreateTime) VALUES {detailSql}").ConfigureAwait(false);
                  
            }, "请求数据批量保存失败").ConfigureAwait(false);
        }


        public async Task AddRequestInfoAsync(IRequestInfo request, IRequestDetail detail)
        {
            if (_options.EnableDefer)
            {
                _deferFlushCollection.Push(request,detail);
            }
            else
            {
                await LoggingSqlOperation(async connection =>
                {
                    await connection.ExecuteAsync("INSERT INTO [RequestInfo] (Id,ParentId,Node,Route,Url,Method,Milliseconds,StatusCode,IP,Port,LocalIP,LocalPort,CreateTime)  VALUES (@Id,@ParentId,@Node, @Route, @Url, @Method, @Milliseconds, @StatusCode, @IP,@Port,@LocalIP,@LocalPort,@CreateTime)", request).ConfigureAwait(false);

                    await connection.ExecuteAsync("INSERT INTO [RequestDetail] (Id,RequestId,Scheme,QueryString,Header,Cookie,RequestBody,ResponseBody,ErrorMessage,ErrorStack,CreateTime)  VALUES (@Id,@RequestId,@Scheme,@QueryString,@Header,@Cookie,@RequestBody,@ResponseBody,@ErrorMessage,@ErrorStack,@CreateTime)", detail).ConfigureAwait(false);

                }, "请求数据保存失败").ConfigureAwait(false);

            } 

        }

        /// <summary>
        /// 获取所有节点信息
        /// </summary>
        /// <returns></returns>
        public async Task<List<NodeInfo>> GetNodesAsync()
        {
            string[] nodeNames = null;
            await LoggingSqlOperation(async connection =>
            {
                nodeNames = (await connection.QueryAsync<string>("Select Distinct Node FROM RequestInfo;").ConfigureAwait(false)).ToArray();
            }, "获取所有节点信息失败").ConfigureAwait(false);

            return nodeNames?.Select(m => new NodeInfo { Name = m }).ToList();
        }


        /// <summary>
        /// 获取Url的平均请求处理时间统计
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<List<RequestAvgResponeTime>> GetRequestAvgResponeTimeStatisticsAsync(RequestInfoFilterOption filterOption)
        {
            string sql = $"Select TOP {filterOption.Take} Url,Avg(Milliseconds) Time FROM RequestInfo {BuildSqlFilter(filterOption)} Group By Url order by Time {BuildSqlControl(filterOption)}";

            TraceLogSql(sql);

            List<RequestAvgResponeTime> result = null;
            await LoggingSqlOperation(async connection =>
            {
                result = (await connection.QueryAsync<RequestAvgResponeTime>(sql).ConfigureAwait(false)).ToList();
            }, "获取Url的平均请求处理时间统计异常").ConfigureAwait(false);

            return result;
        }

        public async Task<List<StatusCodeCount>> GetStatusCodeStatisticsAsync(RequestInfoFilterOption filterOption)
        {
            string where = BuildSqlFilter(filterOption, true);

            var sql = string.Join(" Union ", filterOption.StatusCodes.Select(m => $"Select '{m}' Code,COUNT(1) Total From RequestInfo {where} AND StatusCode = {m}"));

            TraceLogSql(sql);

            List<StatusCodeCount> result = null;
            await LoggingSqlOperation(async connection =>
            {
                result = (await connection.QueryAsync<StatusCodeCount>(sql).ConfigureAwait(false)).ToList();
            }, "获取http状态码数量统计异常").ConfigureAwait(false);

            return result;
        }

        public async Task<List<ResponeTimeGroup>> GetGroupedResponeTimeStatisticsAsync(GroupResponeTimeFilterOption filterOption)
        {
            string where = BuildSqlFilter(filterOption);

            var sqlBuilder = new StringBuilder("Select Name,Total from (", 512);

            var group = filterOption.TimeGroup;
            var groupCount = group.Length / group.Rank;
            for (int i = 0; i < groupCount; i++)
            {
                var min = group[i, 0];
                var max = group[i, 1];
                if (min < max)
                {
                    sqlBuilder.Append($"Select {i + 1} Id,'{min}-{max}' Name, Count(1) Total From RequestInfo {where} AND Milliseconds >= {min} AND Milliseconds < {max} union ");
                }
                else
                {
                    sqlBuilder.Append($"Select {i + 1} Id,'{min}以上' Name, Count(1) Total From RequestInfo {where} AND Milliseconds >= {min} union ");
                }
            }

            var sql = sqlBuilder.Remove(sqlBuilder.Length - 6, 6).Append(")T Order By ID").ToString();

            TraceLogSql(sql);

            List<ResponeTimeGroup> result = null;
            await LoggingSqlOperation(async connection =>
            {
                result = (await connection.QueryAsync<ResponeTimeGroup>(sql).ConfigureAwait(false)).ToList();
            }, "获取http状态码分组统计异常").ConfigureAwait(false);

            return result;
        }



        public async Task<List<UrlRequestCount>> GetUrlRequestStatisticsAsync(RequestInfoFilterOption filterOption)
        {
            string sql = $"Select TOP {filterOption.Take} Url,COUNT(1) as Total From RequestInfo {BuildSqlFilter(filterOption)} Group By Url order by Total {BuildSqlControl(filterOption)};";

            TraceLogSql(sql);

            List<UrlRequestCount> result = null;
            await LoggingSqlOperation(async connection =>
            {
                result = (await connection.QueryAsync<UrlRequestCount>(sql).ConfigureAwait(false)).ToList();
            }).ConfigureAwait(false);

            return result;
        }


        /// <summary>
        /// 控制子句
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        protected string BuildSqlControl(IFilterOption filterOption)
        {
            var builder = new StringBuilder(512);

            if (filterOption is IOrderFilterOption orderFilterOption)
            {
                if (orderFilterOption.IsOrderByField)
                {
                    builder.Append($"ORDER BY {orderFilterOption.GetOrderField()} {(orderFilterOption.IsAscend ? "Asc" : "Desc")} ");
                }
                else
                {
                    builder.Append($"{(orderFilterOption.IsAscend ? "Asc" : "Desc")} ");
                }
            } 

            return builder.ToString();
        }   
        

        /// <summary>
        /// 获取首页数据
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<IndexPageData> GetIndexPageDataAsync(IndexPageDataFilterOption filterOption)
        {
            string where = BuildSqlFilter(filterOption);

            string sql = $@"Select COUNT(1) Total From RequestInfo {where};
                Select COUNT(1) Code404 From RequestInfo {where} AND StatusCode = 404;
                Select COUNT(1) Code500 From RequestInfo {where} AND StatusCode = 500;
                Select Count(1) From ( Select Distinct Url From RequestInfo ) A;
                Select AVG(Milliseconds) ART From RequestInfo {where};";

            TraceLogSql(sql);

            IndexPageData result = new IndexPageData();

            await LoggingSqlOperation(async connection =>
            {
                using (var resultReader = await connection.QueryMultipleAsync(sql).ConfigureAwait(false))
                {
                    result.Total = resultReader.ReadFirstOrDefault<int>();
                    result.NotFound = resultReader.ReadFirstOrDefault<int>();
                    result.ServerError = resultReader.ReadFirstOrDefault<int>();
                    result.APICount = resultReader.ReadFirst<int>();
                    result.ErrorPercent = result.Total == 0 ? 0 : Convert.ToDouble(result.ServerError) / Convert.ToDouble(result.Total);
                    result.AvgResponseTime = double.TryParse(resultReader.ReadFirstOrDefault<string>(), out var avg) ? avg : 0;
                }
            }, "获取首页数据异常").ConfigureAwait(false);

            return result;
        }


        protected async Task LoggingSqlOperation(Func<IDbConnection, Task> func, string message = null, [CallerMemberName]string method = null)
        {
            try
            {
                using (var connection = ConnectionFactory.GetConnection())
                {
                    await func(connection).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Method: {method} Message: {message ?? "数据库操作异常"}");
            }
        }

        protected async Task<T> LoggingSqlOperation<T>(Func<IDbConnection, Task<T>> func, string message = null, [CallerMemberName]string method = null)
        {
            try
            {
                using (var connection = ConnectionFactory.GetConnection())
                {
                    return await func(connection).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Method: {method} Message: {message ?? "数据库操作异常"}");
                throw;
            }
        }

        protected void TraceLogSql(string sql, [CallerMemberName]string method = null)
        {
            Logger.LogTrace($"Class: {nameof(SQLServerStorage)} Method: {method} SQL: {sql}");
        }

        /// <summary>
        /// where子句
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        protected string BuildSqlFilter(IFilterOption filterOption, bool withOutStatusCode = false)
        {
            var builder = new StringBuilder(256);

            if (filterOption is INodeFilterOption nodeFilterOption && nodeFilterOption.Nodes?.Length > 0)
            {
                CheckSqlWhere(builder).Append($"Node in ({string.Join(",", nodeFilterOption.Nodes.Select(m => $"'{m}'"))}) ");
            }

            if (!withOutStatusCode && filterOption is IStatusCodeFilterOption statusCodeFilterOption && statusCodeFilterOption.StatusCodes?.Length > 0)
            {
                if (statusCodeFilterOption.StatusCodes.Length == 1)
                {
                    CheckSqlWhere(builder).Append($"StatusCode = {statusCodeFilterOption.StatusCodes[0]} ");
                }
                else
                {
                    CheckSqlWhere(builder).Append($"StatusCode in ({string.Join(",", statusCodeFilterOption.StatusCodes)}) ");
                }
            }

            if (filterOption is ITimeSpanFilterOption timeSpanFilterOption)
            {
                if (timeSpanFilterOption.StartTime.HasValue)
                {
                    CheckSqlWhere(builder).Append($"CreateTime >= '{timeSpanFilterOption.StartTime.Value.ToString(timeSpanFilterOption.StartTimeFormat)}' ");
                }
                if (timeSpanFilterOption.EndTime.HasValue)
                {
                    CheckSqlWhere(builder).Append($"CreateTime < '{timeSpanFilterOption.EndTime.Value.ToString(timeSpanFilterOption.EndTimeFormat)}' ");
                }
            }

            return builder.ToString();
        }

        protected StringBuilder CheckSqlWhere(StringBuilder builder)
        {
            if (builder.Length == 0)
            {
                builder.Append("WHERE ");
            }
            else
            {
                builder.Append("AND ");
            }
            return builder;
        }


        /// <summary>
        /// 获取请求信息
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<RequestInfoSearchResult> SearchRequestInfoAsync(RequestInfoSearchFilterOption filterOption)
        {
            var whereBuilder = new StringBuilder(BuildSqlFilter(filterOption), 512);

            var sqlBuilder = new StringBuilder("Select * From RequestInfo ", 512);

            if (whereBuilder.Length == 0)
            {
                whereBuilder.Append("Where 1=1 ");
            }

            if (!filterOption.IP.IsEmpty())
            {
                whereBuilder.Append($" AND IP = '{filterOption.IP}' ");
            }

            if (!filterOption.Url.IsEmpty())
            {
                whereBuilder.Append($" AND  Url like '%{filterOption.Url}%' ");
            }

            var where = whereBuilder.ToString();

            sqlBuilder.Append(where);

            var sql = sqlBuilder.ToString();

            TraceLogSql(sql);

            var countSql = "Select count(1) From RequestInfo " + where;
            TraceLogSql(countSql);

            var result = new RequestInfoSearchResult()
            {
                SearchOption = filterOption,
            };

            await LoggingSqlOperation(async connection =>
            { 
                result.AllItemCount = connection.QueryFirstOrDefault<int>(countSql);

                result.List.AddRange((await connection.GetListBySqlAsync<RequestInfo>(sql,"CreateTime desc",filterOption.PageSize,filterOption.Page,result.AllItemCount).ConfigureAwait(false)).ToArray());
            }, "查询请求信息列表异常").ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// 获取请求次数统计
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<RequestTimesStatisticsResult> GetRequestTimesStatisticsAsync(TimeSpanStatisticsFilterOption filterOption)
        {
            var where = BuildSqlFilter(filterOption);

            var dateFormat = GetDateFormat(filterOption);

            string sql = $"Select {dateFormat} KeyField,COUNT(1) ValueField From RequestInfo {where} Group by {dateFormat};";

            TraceLogSql(sql);

            var result = new RequestTimesStatisticsResult()
            {
                Type = filterOption.Type,
            };

            await LoggingSqlOperation(async connection =>
            {
                result.Items = new Dictionary<string, int>();
                (await connection.QueryAsync<KVClass<string, int>>(sql).ConfigureAwait(false)).ToList().ForEach(m =>
                {
                    result.Items.Add(m.KeyField.Split('-').Last().ToInt().ToString(), m.ValueField);
                });
            }, "获取请求次数统计异常").ConfigureAwait(false);

            return result;
        } 

        /// <summary>
        /// 获取响应时间统计
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<ResponseTimeStatisticsResult> GetResponseTimeStatisticsAsync(TimeSpanStatisticsFilterOption filterOption)
        {
            var where = BuildSqlFilter(filterOption);

            var dateFormat = GetDateFormat(filterOption);

            string sql = $"Select {dateFormat} KeyField,AVG(Milliseconds) ValueField From RequestInfo {where} Group by {dateFormat};";

            TraceLogSql(sql);

            var result = new ResponseTimeStatisticsResult()
            {
                Type = filterOption.Type,
            };

            await LoggingSqlOperation(async connection =>
            {
                result.Items = new Dictionary<string, int>();
                (await connection.QueryAsync<KVClass<string, int>>(sql).ConfigureAwait(false)).ToList().ForEach(m =>
                {
                    result.Items.Add(m.KeyField.ToInt().ToString(), m.ValueField);
                });
            }, "获取响应时间统计异常").ConfigureAwait(false);

            return result;
        }


        private class KVClass<TKey, TValue>
        {
            public TKey KeyField { get; set; }
            public TValue ValueField { get; set; }
        }

        private static string GetDateFormat(TimeSpanStatisticsFilterOption filterOption)
        {
            string dateFormat;
            switch (filterOption.Type)
            {
                case TimeUnit.Hour:
                    dateFormat = "DATENAME(HOUR, CreateTime)";
                    break;

                case TimeUnit.Month:
                    dateFormat = "CONVERT(varchar(7),CreateTime, 120)";
                    break;

                case TimeUnit.Year:
                    dateFormat = "";
                    break;

                case TimeUnit.Day:
                default:
                    dateFormat = "CONVERT(varchar(100),CreateTime, 23)";
                    break;
            }

            return dateFormat;
        }

        public async Task<int> GetRequestCountAsync(RequestCountFilterOption filterOption)
        {
            var sql = $"SELECT COUNT(1) FROM RequestInfo {BuildSqlFilter(filterOption)}";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => await connection.QueryFirstOrDefaultAsync<int>(sql).ConfigureAwait(false));
        }

        /// <summary>
        /// 获取白名单外的获取请求总次数
        /// </summary>
        /// <param name="filterOption"></param>
        /// <returns></returns>
        public async Task<(int Max, int All)> GetRequestCountWithWhiteListAsync(RequestCountWithListFilterOption filterOption)
        {
            var ipFilter = $"({string.Join(",", filterOption.List.Select(x=> $"'{x}'" ))})";
            if (filterOption.InList)
            {
                ipFilter = "IP IN " + ipFilter;
            }
            else
            {
                ipFilter = "IP NOT IN " + ipFilter;
            }

            var sql = $"SELECT TOP 1 COUNT(1) FROM RequestInfo {BuildSqlFilter(filterOption)} AND {ipFilter} Group By IP Order BY COUNT(1) Desc";
            TraceLogSql(sql);

            var max = await LoggingSqlOperation(async connection => await connection.QueryFirstOrDefaultAsync<int>(sql).ConfigureAwait(false));


            sql = $"SELECT COUNT(1) TOTAL FROM RequestInfo {BuildSqlFilter(filterOption)} AND {ipFilter}";
            TraceLogSql(sql);
            var all = await LoggingSqlOperation(async connection => await connection.QueryFirstOrDefaultAsync<int>(sql).ConfigureAwait(false));
            return (max, all);
        }

        public async Task<int> GetTimeoutResponeCountAsync(RequestCountFilterOption filterOption, int timeoutThreshold)
        {
            var where = BuildSqlFilter(filterOption);
            var sql = $"SELECT COUNT(1) FROM RequestInfo {(string.IsNullOrWhiteSpace(where) ? "WHERE" : where)} AND Milliseconds >= {timeoutThreshold}";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => await connection.QueryFirstOrDefaultAsync<int>(sql).ConfigureAwait(false));
        }

        public async Task<bool> AddMonitorJob(IMonitorJob job)
        {
            job.Id = MD5_16(Guid.NewGuid().ToString());

            string sql = $@"Insert Into MonitorJob 
            (Id,Title,Description,CronLike,Emails,WebHook,Mobiles,Status,Nodes,PayLoad,CreateTime)
             Values (@Id,@Title,@Description,@CronLike,@Emails,@WebHook,@Mobiles,@Status,@Nodes,@PayLoad,@CreateTime)";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

            await connection.ExecuteAsync(sql, job).ConfigureAwait(false)

            ) > 0).ConfigureAwait(false);

        }

        public async Task<bool> UpdateMonitorJob(IMonitorJob job)
        {
            string sql = $@"Update MonitorJob 

                Set Title = @Title,Description = @Description,CronLike = @CronLike,Emails = @Emails,WebHook = @WebHook, Mobiles = @Mobiles,Status= @Status,Nodes = @Nodes,PayLoad = @PayLoad 

                Where Id = @Id ";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

            await connection.ExecuteAsync(sql, job).ConfigureAwait(false)

            ) > 0).ConfigureAwait(false);
        }

        public async Task<IMonitorJob> GetMonitorJob(string Id)
        {
            string sql = $@"Select * From MonitorJob Where Id = '{Id}' " ;

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

              await connection.QueryFirstOrDefaultAsync<MonitorJob>(sql).ConfigureAwait(false)

            )).ConfigureAwait(false);
        }

        public async Task<List<IMonitorJob>> GetMonitorJobs()
        {
            string sql = $@"Select * From MonitorJob ";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

            await connection.QueryAsync<MonitorJob>(sql).ConfigureAwait(false)

            ).ToList().Select(x => x as IMonitorJob).ToList()).ConfigureAwait(false);
        }

        public async Task<bool> DeleteMonitorJob(string Id)
        {
            string sql = $@"Delete From MonitorJob Where Id = '{Id}' ";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection =>
            (await connection.ExecuteAsync(sql).ConfigureAwait(false)) > 0).ConfigureAwait(false);
        }

        public async Task<SysUser> CheckLogin(string Username, string Password)
        {
            string sql = " Select * From SysUser Where UserName = @UserName AND Password = @Password ";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

              await connection.QueryFirstOrDefaultAsync<SysUser>(sql, new { Username, Password }).ConfigureAwait(false)

            )).ConfigureAwait(false);

        }

        public async Task<bool> UpdateLoginUser(SysUser model)
        {
            string sql = " Update SysUser Set UserName = @UserName , Password = @Password  Where Id = @Id ";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

              await connection.ExecuteAsync(sql, model).ConfigureAwait(false)

             ) > 0).ConfigureAwait(false);

        } 

        public async Task<SysUser> GetSysUser(string UserName)
        {
            string sql = " Select * From SysUser Where UserName = @UserName";

            TraceLogSql(sql);

            return await LoggingSqlOperation(async connection => (

              await connection.QueryFirstOrDefaultAsync<SysUser>(sql, new { UserName }).ConfigureAwait(false)

            )).ConfigureAwait(false);
        }

        private string MD5_16(string source)
        {
            MD5CryptoServiceProvider md5 = new MD5CryptoServiceProvider();
            string val = BitConverter.ToString(md5.ComputeHash(UTF8Encoding.Default.GetBytes(source)), 4, 8).Replace("-", "").ToLower();
            return val;
        }

        public async Task<(IRequestInfo, IRequestDetail)> GetRequestInfoDetail(string Id)
        {
            string sql = " Select * From RequestInfo Where Id = @Id";

            TraceLogSql(sql);  

             var requestInfo = await LoggingSqlOperation(async connection => (

              await connection.QueryFirstOrDefaultAsync<RequestInfo>(sql, new { Id }).ConfigureAwait(false)

            )).ConfigureAwait(false);

            string detailSql = " Select * From RequestDetail Where RequestId = @Id";

            TraceLogSql(detailSql);

            var requestDetail = await LoggingSqlOperation(async connection => (

             await connection.QueryFirstOrDefaultAsync<RequestDetail>(detailSql, new { Id }).ConfigureAwait(false)

           )).ConfigureAwait(false);

            return (requestInfo,requestDetail);

        }

        public async Task<IRequestInfo> GetRequestInfo(string Id)
        {
            string sql = " Select * From RequestInfo Where Id = @Id";

            TraceLogSql(sql);

            var requestInfo = await LoggingSqlOperation(async connection => (

             await connection.QueryFirstOrDefaultAsync<RequestInfo>(sql, new { Id }).ConfigureAwait(false)

           )).ConfigureAwait(false); 

            return requestInfo;

        }

        public Task<List<IRequestInfo>> GetRequestInfoByParentId(string ParentId)
        {
            throw new NotImplementedException();
        }
    }
}