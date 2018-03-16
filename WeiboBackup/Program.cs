using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using static System.Console;

namespace WeiboBackup
{
    // Model，因改用 dynamic 已不再需要
    //
    //class Page
    //{
    //    public Data data { get; set; }
    //}
    //class Data
    //{
    //    public Card[] cards { get; set; }
    //}
    //class Card
    //{
    //    public MBlog mblog { get; set; }
    //}
    //class MBlog
    //{
    //    public long id { get; set; }
    //    public string bid { get; set; }
    //    public MBlog retweeted_status { get; set; }
    //    public Picture[] pics { get; set; }
    //    public bool isLongText { get; set; }
    //    public string text { get; set; }
    //    public int comments_count { get; set; }
    //    public int reposts_count { get; set; }
    //}
    //class Picture
    //{
    //    public Picture large { get; set; }
    //    public string url { get; set; }
    //}
    //class Comments
    //{
    //    public int ok { get; set; }
    //}

    class Program
    {
        // 网络请求是同步的
        // 每个请求都会 sleep，防止频率过高而被 ban
        // 应该还有缩短的余地，具体时间在此范围内随机
        const int SLEEP_MIN = 3600;
        const int SLEEP_MAX = 5000;
        const int RETRY_LIMIT = 5;
        // 如使用相对路径，请注意工作目录
        const string DATA_PATH = "Weibo";

        static Random random = new Random();

        static void Main(string[] args)
        {
            var config = File.ReadAllLines("config");
            long uid = long.Parse(config[0]);
            string cookie = config[1];

            Directory.CreateDirectory(DATA_PATH);

            HttpWebRequest req = null;
            WebClient webClient = new WebClient();

            int page = 0, post = 0;
            while (true)
            {
                string json = null;
                dynamic weiboPage = null;
                page += 1;
                try
                {
                    json = GetWeiboPage(page);
                    weiboPage = JObject.Parse(json);
                }
                catch (JsonException)
                {
                    WriteLine("(JSON 语法错误）");
                    continue;
                }
                catch (Exception ex)
                {
                    WriteLine(ex);
                    break;
                }

                if (weiboPage?.data?.cards == null || weiboPage.data.cards.Count == 0)
                {
                    WriteLine("完成");
                    break;
                }

                foreach (var card in weiboPage.data.cards)
                {
                    post += 1;
                    Write($"微博：{post}");

                    var mblog = card.mblog;

                    Directory.CreateDirectory($@"{DATA_PATH}/{mblog.id}");

                    try
                    {
                        try
                        {
                            if ((bool)mblog.isLongText)
                            {
                                mblog.text =
                                    (JObject.Parse(GetLongText(mblog.id)) as dynamic)
                                    .data.longTextContent;
                                Write("，长微博");
                            }
                            if (mblog.retweeted_status != null)
                            {
                                if (mblog.retweeted_status.deleted > 0)
                                {
                                    Write("，转发来源已删除");
                                }
                                else
                                {
                                    if ((bool)mblog.retweeted_status.isLongText)
                                    {
                                        mblog.retweeted_status.text =
                                            (JObject.Parse(GetLongText(mblog.retweeted_status.id)) as dynamic)
                                            .data.longTextContent;
                                        Write("，转发来源长微博");
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            Write("（JSON 语法错误）");
                        }

                        if (mblog.visible.type == 6)
                        {
                            Write("，好友圈");
                        }
                        else if (mblog.visible.type == 1)
                        {
                            Write("，私密");
                        }
                        else if (mblog.visible.type > 0)
                        {
                            Write("，可见性未知");
                        }

                        if (mblog.pics != null)
                        {
                            GetPictures(mblog);
                        }
                        if (mblog.retweeted_status?.pics != null)
                        {
                            GetPictures(mblog.retweeted_status, mblog.id);
                        }

                        if (mblog.comments_count > 0)
                        {
                            File.WriteAllText(
                                $@"{DATA_PATH}/{mblog.id}/comments.json",
                                JsonConvert.SerializeObject(GetComments(mblog.id)));
                        }
                        if (mblog.reposts_count > 0)
                        {
                            File.WriteAllText(
                                $@"{DATA_PATH}/{mblog.id}/reposts.json",
                                JsonConvert.SerializeObject(GetReposts(mblog.id)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Write("（发生未知异常，详见该微博目录下 exception 文件）");
                        File.WriteAllText(
                            $@"{DATA_PATH}/{mblog.id}/exception",
                            ex.ToString());
                    }

                    File.WriteAllText(
                        $@"{DATA_PATH}/{mblog.id}/status.json",
                        mblog.ToString());

                    WriteLine("   ");
                }

            }

            ReadKey(true);

            string GetWeiboPage(int id)
            {
                return HttpGetString(
                    $"https://m.weibo.cn/api/container/getIndex" +
                    $"?type=uid" +
                    $"&value={uid}" +
                    $"&containerid=107603{uid}" +
                    $"&page={id}");
            }
            string GetLongText(long id)
            {
                return HttpGetString(
                    $"https://m.weibo.cn/statuses/extend" +
                    $"?id={id}");
            }
            void GetPictures(dynamic mblog, long id = 0)
            {
                if (id == 0)
                {
                    id = mblog.id;
                }
                Write("，" + (mblog.id == id ? "" : "转发来源") + "图片：0...");
                var count = 0;
                foreach (var pic in mblog.pics)
                {
                    try
                    {
                        HttpGetFile(
                            pic.large.url.Value,
                            $@"{DATA_PATH}/{id}/{Path.GetFileName(pic.large.url.Value)}");
                        count += 1;
                        Write($"\b\b\b\b{count}...");
                    }
                    catch { }
                }
                Write("\b\b\b");
            }
            IEnumerable<object> GetComments(long id)
            {
                Write("，评论：0...");
                var lastCount = 0;
                var commentPage = 1;
                var commentList = new List<object>();
                do
                {
                    try
                    {
                        var json = HttpGetString(
                            $"https://m.weibo.cn/api/comments/show" +
                            $"?id={id}" +
                            $"&page={commentPage}");
                        dynamic data = JsonConvert.DeserializeObject(json);

                        if (data?.ok.Type == JTokenType.Integer && data.ok > 0)
                        {
                            commentList.AddRange(data.data.data);
                            Write($"\b\b\b{new string('\b', lastCount.ToString().Length)}{commentList.Count}...");
                            lastCount = commentList.Count;
                        }
                        else
                        {
                            break;
                        }

                        commentPage += 1;
                    }
                    catch
                    {
                        break;
                    }
                } while (true);
                Write("\b\b\b");
                return commentList;
            }
            IEnumerable<object> GetReposts(long id)
            {
                Write("，转发：0...");
                var lastCount = 0;
                var repostPage = 1;
                var repostList = new List<object>();
                do
                {
                    try
                    {
                        var json = HttpGetString(
                            $"https://m.weibo.cn/api/statuses/repostTimeline" +
                            $"?id={id}" +
                            $"&page={repostPage}");
                        dynamic data = JsonConvert.DeserializeObject(json);

                        if (data?.ok.Type == JTokenType.Integer && data.ok > 0)
                        {
                            repostList.AddRange(data.data.data);
                            Write($"\b\b\b{new string('\b', lastCount.ToString().Length)}{repostList.Count}...");
                            lastCount = repostList.Count;
                        }
                        else
                        {
                            break;
                        }

                        repostPage += 1;
                    }
                    catch
                    {
                        break;
                    }
                } while (true);
                Write("\b\b\b");
                return repostList;
            }

            string HttpGetString(string url)
            {
                var retryCount = 0;
                while (retryCount < RETRY_LIMIT)
                {
                    var wait = random.Next(SLEEP_MIN, SLEEP_MAX);
                    Thread.Sleep(wait);

                    try
                    {
                        req = WebRequest.CreateHttp(url);
                        var cookies = new CookieContainer();
                        cookies.SetCookies(new Uri(url), cookie.Replace(';', ','));
                        req.CookieContainer = cookies;
                        return new StreamReader(
                            (req.GetResponse() as HttpWebResponse).GetResponseStream()
                        ).ReadToEnd();
                    }
                    catch (WebException ex)
                    {
                        Debug.WriteLine(ex);
                        retryCount += 1;
                    }
                }
                WriteLine($"已达到 {RETRY_LIMIT} 次重试的限制，跳过此请求");
                return null;
            }
            void HttpGetFile(string url, string savePath)
            {
                var retryCount = 0;
                while (retryCount < RETRY_LIMIT)
                {
                    var wait = random.Next(SLEEP_MIN, SLEEP_MAX);
                    Thread.Sleep(wait);

                    try
                    {
                        webClient.DownloadFile(url, savePath);
                        return;
                    }
                    catch (WebException ex)
                    {
                        Debug.WriteLine(ex);
                        retryCount += 1;
                    }
                }
                WriteLine($"已达到 {RETRY_LIMIT} 次重试的限制，跳过此请求");
            }
        }

    }
}
