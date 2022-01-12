﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using LinqToDB;

namespace AspPix.Controllers
{



    [Route("/pix/api/[controller]")]
    [ApiController]
    public class ImgController : ControllerBase
    {
        private readonly IHttpClientFactory _clientFactory;

        public ImgController(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        [HttpGet]
        public async Task<ActionResult> Get([FromQuery] string path, [FromQuery] string path2, [FromQuery] int id)
        {
            //"https://i.pximg.net/c/540x540_70/img-master/img/2020/04/24/22/48/16/81033008_p0_master1200.jpg"

            using var db = Info.DbCreateFunc();

            var img = await db.GetTable<Info.PixImg>().Where(p => p.Id == id).FirstOrDefaultAsync();


            if (img is not null)
            {
                return new FileContentResult(img.Img, MediaTypeNames.Image.Jpeg);
            }

            var host = "https://morning-bird-d5a7.sparkling-night-bc75.workers.dev/";
            try
            {
                var by = await Info.GetImg(_clientFactory.CreateClient(), host+ Fs.PixFunc.base64Decode(path), host+ Fs.PixFunc.base64Decode(path2));

                db.InsertOrReplace(new Info.PixImg { Id = id, Img = by });

                return new FileContentResult(by, MediaTypeNames.Image.Jpeg);
            }
            catch (HttpRequestException)
            {

            }
            catch (TaskCanceledException)
            {

            }

            return NotFound();
        }
    }
}
