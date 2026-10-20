"""Read-only public directory probe. Fixed origins, no redirects, no code execution."""
import hashlib
import json
import time
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET
from html.parser import HTMLParser
from pathlib import Path

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = set()
    def handle_starttag(self, tag, attrs):
        if tag != 'a':
            return
        href = dict(attrs).get('href', '')
        url = urllib.parse.urlsplit(urllib.parse.urljoin('https://oi-wiki.org/', href))
        if url.scheme == 'https' and url.netloc == 'oi-wiki.org' and not url.query:
            self.links.add('https://oi-wiki.org' + url.path)

output = Path('artifacts/p8-development/20261019-baseline/oi')
output.mkdir(parents=True, exist_ok=True)
opener = urllib.request.build_opener(NoRedirect())
records = []
for name, url in [('home', 'https://oi-wiki.org/'), ('sitemap', 'https://oi-wiki.org/sitemap.xml')]:
    start = time.monotonic()
    try:
        with opener.open(url, timeout=30) as response:
            data = response.read(2 * 1024 * 1024 + 1)
        if len(data) > 2 * 1024 * 1024:
            raise ValueError('response_limit')
        (output / (name + '.txt')).write_bytes(data)
        if name == 'home':
            parser = Links()
            parser.feed(data.decode('utf-8'))
            urls = sorted(parser.links)
        else:
            urls = sorted({v.text for v in ET.fromstring(data).iter() if v.tag.endswith('}loc')})
        (output / (name + '-urls.json')).write_text(json.dumps(urls, indent=2), encoding='utf-8')
        records.append(dict(name=name, url=url, bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                            directoryEntries=len(urls), elapsedSeconds=time.monotonic()-start,
                            status='retrieved_directory_only', fullBodyCoverage=False))
    except Exception as error:
        records.append(dict(name=name, url=url, status='failed', error=type(error).__name__, elapsedSeconds=time.monotonic()-start))
(output / 'inventory.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
print(json.dumps(records))
