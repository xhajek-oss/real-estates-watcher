const puppeteer = require("puppeteer");
const fs = require('fs');

const defaultFileEncoding = "utf8";

function parseCookies(pathToCookiesFile) {
    try {
        if (fs.existsSync(pathToCookiesFile)) {
            let cookiesString = fs.readFileSync(pathToCookiesFile, defaultFileEncoding);

            return JSON.parse(cookiesString);
        }
    } catch (err) {
        return undefined;
    }
}

(async function () {
    let browser;

    try {
        const isCi = process.env.CI === 'true';
        const scrapingTimeoutSeconds = Number.parseInt(process.argv[2], 10) || 30;
        const navigationTimeoutMs = Math.max(5000, (scrapingTimeoutSeconds - 10) * 1000);
        const renderDelayMs = Math.min(5000, Math.max(1000, scrapingTimeoutSeconds * 100));

        browser = await puppeteer.launch({
            ignoreDefaultArgs: ['--disable-extensions --user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36'],
            headless: isCi,
            args: isCi ? ['--no-sandbox', '--disable-setuid-sandbox'] : []
        });

        const page = await browser.newPage();

        const cookies = parseCookies(process.argv[4]);
        if (cookies) {
            await page.setCookie(...cookies);
        }

        await page.goto(process.argv[3], {
            waitUntil: 'domcontentloaded',
            timeout: navigationTimeoutMs
        });

        // Give client-side rendered listing pages a short, bounded window to populate the DOM.
        await new Promise(resolve => setTimeout(resolve, renderDelayMs));

        console.log(await page.content());
    } catch (err) {
        console.error(err);
        process.exitCode = 1;
    } finally {
        if (browser) {
            await browser.close();
        }
    }
})();
