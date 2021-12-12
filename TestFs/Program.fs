﻿open System
open System.Net.Http
open System.Threading.Tasks
open System.Threading
open FSharp.Core
open System
open System.IO
open System.Linq
open FSharp.Control
open System.Security.Cryptography
open System.Text
open FSharp.NativeInterop
open System.Buffers
open System.Net.Sockets
open System.Security.Authentication
open System.Net.Security
open System.Net
open System.Net.Http.Headers
open LinqToDB;
open LinqToDB.Mapping;
open LinqToDB.Data
open System.Text.RegularExpressions
open System.Threading.Channels
open System.Collections

module PixSql =

    type Pixiv2 = {
        
        [<PrimaryKey>]
        Id:int
        Mark:int
        Date:DateTime
        ImgEN:byte
    }
    
    type PixivTag = {
        [<PrimaryKey>]
        Id:int
        Tag:string
    }

    type PixivTagHas = {
        
        [<PrimaryKey>]
        ItemId:int

        [<PrimaryKey>]
        TagId:int
    }

    type PixivHtml = {
        pix:Pixiv2
        tag:string[]
    }

    let getTagHash(tag:string) =
        tag
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> fun v -> 
            BitConverter.ToInt32(v,0)

    let runInsetDb count (createDb:unit -> DataConnection) (reader:ChannelReader<PixivHtml>) (log:string-> unit) =
        

        let loadCount() =
            let vs = Generic.List<PixivHtml>()

            
            while vs.Count <> count do
                let mutable v = Unchecked.defaultof<PixivHtml>

                while reader.TryRead(&v) = false do
                    Thread.Sleep(TimeSpan(0,0,5))
                
                vs.Add(v)

            vs

        let inset(vs:Generic.List<PixivHtml>) = 
            
            
            let dic = Generic.Dictionary<string,int>()
                     
            let createPixTagHas() =
                let createTagHash id tag =                    
                    let setdic(tag)=
                        let mutable v = Unchecked.defaultof<int>
                    
                        if dic.TryGetValue(tag, &v) then
                            v
                        else
                            let c = getTagHash(tag)
                            dic.Add(tag, c)
                            c                   
                    tag
                    |> Array.toSeq
                    |> Seq.map (fun v -> {ItemId = id; TagId = setdic v})
                vs
                |> Seq.map (fun p -> createTagHash p.pix.Id p.tag)
                |> Seq.concat

            
            use db = createDb()

            
            let sert v =
                try
                    db.Insert(v) |> ignore
                    ()
                with
                | :? MySql.Data.MySqlClient.MySqlException as e when e.Message.StartsWith("Duplicate entry") -> ()
              

            let sertRe v =
                db.InsertOrReplace(v) |> ignore
                ()

            vs 
            |> Seq.map (fun p -> p.pix)
            |> Seq.iter (fun v -> sertRe(v))


            createPixTagHas()
            |> Seq.iter (fun v -> sert v)

            dic 
            |> Seq.map (fun d -> {Id = d.Value; Tag = d.Key})
            |> Seq.iter (fun v -> sert v)




        
        let loop() =
            while true do
                let vs = loadCount()
                inset vs

                log $"{DateTime.Now}:DB Last Id:{vs.Last().pix.Id}"


        let th = new Thread(fun () -> loop())

        th.Start()





module PixParse =
    let asDateTimeFromUri (uri:Uri) =
        let n (s:string) = Int32.Parse(s.Trim('/'))

        match uri.Segments with
        | [|_;_;_;year;month;day;hour;minute;second;_|] -> (n(year), n(month), n(day), n(hour), n(minute), n(second)) |> DateTime
        | _ -> raise (ArgumentException("uri 路径应该没有datetime"))

    let asPathFromDateTime (date:DateTime) =
        let toString n =
            match n.ToString() with
            | s when s.Length = 1 -> "0" + s
            | s -> s


        [date.Year;date.Month; date.Day; date.Hour;date.Minute;date.Second]
        |> List.map (fun n -> toString n)
        |> List.toSeq
        |> String.concat "/"

    let getImgUri date id imgEN =
        let ex = if imgEN = 0uy then "jpg" else "png"

        let path = asPathFromDateTime date

        $"/img-original/img/{path}/{id}_p0.{ex}"

    let getImgUriSmall date id b =
        let ex = if b then "_master1200.jpg" else "_p0_master1200.jpg"

        let path = asPathFromDateTime date

        $"/c/540x540_70/img-master/img/{path}/{id}{ex}"


    
    
    
    let re_original = new Regex(@"""original"":""([^""]+)""");
    
    
    let re_mark = new Regex(@"""bookmarkCount"":(\d+),");
    
    
    let re_tags = new Regex(@"""tags"":(\[\{[^\]]+\}\]),");
    
    let getMarkCount s = 
        Int32.Parse(re_mark.Match(s).Groups.[1].Value);
    

    let getUri s =
        new Uri(re_original.Match(s).Groups.[1].Value)

    let getIsjpg (uri:Uri) =
        if uri.AbsolutePath.EndsWith(".jpg") then 0uy else 1uy

    let getTag s =
        
        let getArray (s:string) =
            use js = Json.JsonDocument.Parse(s)
            js.RootElement.EnumerateArray().Select(fun p -> p.GetProperty("tag").GetString()).ToArray()

        match re_tags.Match(s) with
        | m when m.Success -> getArray m.Groups.[1].Value
        | _ -> [||]

    let getPixiv2 s id :PixSql.Pixiv2 =
        let mark = getMarkCount s

        let uri = getUri s

        let date = asDateTimeFromUri uri

        let isjpg = getIsjpg uri

        {Id = id; Mark = mark; Date = date; ImgEN = isjpg}

        
    let getPixivHtml s id :PixSql.PixivHtml =
        {pix = getPixiv2 s id; tag = getTag s}
            

             



module PixHTTP =

    

    let createHttpMessageHandler connentFunc = 
        let e = new SocketsHttpHandler()

        e.AutomaticDecompression <- DecompressionMethods.All

        e.UseProxy <- false

        e.ConnectCallback <- connentFunc
        e

    let createGetHttpRequest uri referer =
    
        let setHeaders (headers: HttpHeaders) (referer:string) =
            headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
        
            headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
        
            headers.Add("Accept-Charset", "utf-8");
        
            headers.Add("Accept-Encoding", "gzip, deflate, br");
        
            headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.1; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.75 Safari/537.36");
        
            headers.Add("Referer", referer);
            ()

        let request = new HttpRequestMessage()

        request.Method <- HttpMethod.Get

        request.RequestUri <- uri

        request.Version <- HttpVersion.Version20

        request.VersionPolicy <- HttpVersionPolicy.RequestVersionOrHigher
    
        setHeaders request.Headers referer

        request


    let createConnectFunc (dns:string) (port:int) (sni:string) =

        let func info token =
            let createOptions sni =
                let op = new SslClientAuthenticationOptions()
                op.TargetHost <- sni
                op.ApplicationProtocols <- [SslApplicationProtocol.Http2].ToList()
                op

            backgroundTask{
       
                let socket = new Socket(SocketType.Stream, ProtocolType.Tcp)

                do! socket.ConnectAsync(dns, port)

                let stream = 
                    new NetworkStream(socket, true) 
                    |> fun v -> new SslStream(v, false) 
        
                do! stream.AuthenticateAsClientAsync(createOptions(sni), token)

                return (stream :> Stream)
            }
            |> ValueTask<Stream>

        Func<_,_,_>(func)

    let createGetHTMLFunc (baseUri:Uri) dns port sni referer =

        let connectFunc = createConnectFunc dns port sni
        let handle = createHttpMessageHandler connectFunc
        let http = new HttpMessageInvoker(handle)

        let ex b n = if b then () else raise (HttpRequestException(n.ToString(), null, Nullable(n)))

        fun (id:int) ->
            backgroundTask{
                let uri = new Uri(baseUri, id.ToString())
                let request = createGetHttpRequest uri referer
                let! response = http.SendAsync(request, CancellationToken.None)
                ex response.IsSuccessStatusCode response.StatusCode
                return! response.Content.ReadAsStringAsync()
            }
    
    
    let getDateTimeId (http: int -> Task<string>) desDateTime =
        
        let MIN = 80000000

        let SPAN = 1000

        let LOOP = 10

        let asDateTime (date:DateTime) = new DateTime(date.Year, date.Month, date.Day)          

        let rec getDateTime left right :Task<DateTime> =
            let onRec() = getDateTime (left + 1) (right)
            
            backgroundTask{
                if left > right then
                    return DateTime.Now.AddDays(1.0)
                else
                    try
                        let! s = http(left)
                        return PixParse.getUri s
                                |> PixParse.asDateTimeFromUri
                                |> asDateTime 
                    with
                    | :? HttpRequestException -> return! onRec()
                    | :? TaskCanceledException -> return! onRec()
            }

        let getDateTime id = getDateTime id (id + LOOP)

        let rec getMaxId min span desDateTime =
            backgroundTask{
                let! vd = getDateTime min
               
                if vd < desDateTime then
                    let n = span * 2
                    return! getMaxId (min + n) n desDateTime

                else
                    return (min - span, min)
            }
            
        let rec get min max desDateTime =
            backgroundTask{
                if min >= max then
                    return -1;
                else
                    let n = min + ((max - min) / 2)

                    match! getDateTime n with
                    | v when v < desDateTime -> return! get n max desDateTime
                    | v when v > desDateTime -> return! get min n desDateTime
                    | v -> return n
            }

        
        backgroundTask{
        
            let vd = asDateTime desDateTime

            let! (min, max) = getMaxId MIN SPAN vd

            return! get min max vd
        }

  

module PixLoad =
    
    

    let rec load (notfoundCount:int) (id:int) (http:int -> Task<string>) (writer:ChannelWriter<PixSql.PixivHtml>) (log:string->unit) =
        backgroundTask{
            try
                let! s = http id

                let v = PixParse.getPixivHtml s id 

                do! writer.WriteAsync(v)

                log $"{DateTime.Now}:Load Id:{id}"

                return! load 0 (id + 1) http writer log
            with
            | :? HttpRequestException 
                as e
                when e.StatusCode = Nullable(HttpStatusCode.NotFound)
                -> if notfoundCount = 1000 then () else return! load (notfoundCount + 1) (id + 1) http writer log 
            | :? HttpRequestException -> return! load 0 id http writer log
            | :? TaskCanceledException -> return! load 0 id http writer log
        }

    let runLoad id http writer log =
        load 0 id http writer log

module PixCrawling =
    
    let run() =
        let db()  = 
            let v = new DataConnection(ProviderName.MySql, $"Host=192.168.0.101;Port=3306;User=myuser;Password=mypass;Database=mysql;SslMode=none")
            v.CommandTimeout<-0
            v
        let http = PixHTTP.createGetHTMLFunc (new Uri("http://www.pixiv.net/artworks/")) "www.pixivision.net" 443 "www.pixivision.net" "https://www.pixivision.net" 

        let ch = Channel.CreateBounded<PixSql.PixivHtml>(100)

       
        let one desDateTime log =
            backgroundTask{
                let! id = PixHTTP.getDateTimeId http desDateTime
                do! PixLoad.runLoad id http ch.Writer log
            }

        let mutable dblog = ""
        let mutable onedaylog = ""
        let mutable threedaylog = ""
        let mutable sevendaylog = ""

        let rec logLine() =
            backgroundTask{
            
                do! Task.Delay(TimeSpan(0,0,5))

                Console.WriteLine($"{dblog} {onedaylog} {threedaylog} {sevendaylog}")

                return! logLine()
            }


        PixSql.runInsetDb 10 db ch.Reader (fun e-> dblog <- e)

        logLine() |> ignore

        one (DateTime.Now) (fun e -> onedaylog <- e) |> ignore
        one (DateTime.Now.AddDays(-3.0)) (fun e -> threedaylog <- e) |> ignore
        one (DateTime.Now.AddDays(-7.0)) (fun e -> sevendaylog <- e) |> ignore
       




[<EntryPoint>]
let main argv =
    
    
   

    let a = PixCrawling.run()

    Console.ReadLine();

    

    0