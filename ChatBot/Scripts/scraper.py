import asyncio
import json
import re
import hashlib
import logging
from pathlib import Path
from typing import Set
import random
import sys

from playwright.async_api import async_playwright, TimeoutError as PlaywrightTimeoutError
from bs4 import BeautifulSoup

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class WebScraper:
    def __init__(self, config_path: str):
        self.load_config(config_path)
        self.cache_file = Path('wwwroot/scraped_content.txt')
        self.latest_content = ''
        self.user_agents = [
            'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36',
            'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15',
            'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/115.0'
        ]
        self.load_cache()

    def load_config(self, config_path: str):
        try:
            with open(config_path, 'r') as f:
                config = json.load(f)
            self.urls = config.get('ScraperSettings', {}).get('UrlsToScrape', [])
            self.section_mappings = config.get('SectionMappings', {}).get('inboxtechs', {})
            self.job_openings = config.get('JobOpeningsStatus', {})
            if not self.section_mappings:
                logger.warning("No section mappings found. Using default mappings.")
                self.section_mappings = {
                    'About': ['About', 'About Us', 'Who We Are', 'Our Story'],
                    'Services': ['Services', 'Our Services', 'What We Offer', 'Solutions'],
                    'Products': ['Products', 'Our Products', 'Solutions'],
                    'Jobs': ['Careers', 'Job Openings', 'Join Us', 'Jobs'],
                    'Contact': ['Contact', 'Contact Us', 'Get in Touch'],
                    'Industries': ['Industries', 'Sectors', 'Markets'],
                    'Awards': ['Awards', 'Achievements', 'Recognitions']
                }
            if not self.urls:
                raise ValueError("No URLs provided in configuration.")
        except Exception as e:
            logger.error(f"Failed to load config: {e}")
            raise

    def load_cache(self):
        if self.cache_file.exists():
            self.latest_content = self.cache_file.read_text(encoding='utf-8')
            logger.info(f"Loaded cached content from {self.cache_file}")

    def compute_md5(self, content: str) -> str:
        return hashlib.md5(content.encode('utf-8')).hexdigest()

    def get_section_name(self, url: str, html: str) -> str:
        from urllib.parse import urlparse
        path = urlparse(url).path.lower()
        for section, names in self.section_mappings.items():
            for name in names:
                if name.replace(' ', '-').lower() in path or name.replace(' ', '').lower() in path:
                    return section
        soup = BeautifulSoup(html, 'html.parser')
        heading = soup.find(['h1', 'h2', 'h3', 'h4', 'h5', 'h6'])
        if heading:
            heading_text = heading.get_text(strip=True).lower()
            for section, names in self.section_mappings.items():
                if any(name.lower() in heading_text for name in names):
                    return section
        return 'GENERAL CONTENT'

    async def extract_section(self, html: str, section_name: str, unique_items: Set[str]) -> str:
        soup = BeautifulSoup(html, 'html.parser')
        content = [f"🔸 {section_name.upper()}:"]  # Section title
        for tag in soup.find_all(['p', 'div', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6']):
            text = tag.get_text(strip=True)
            if 20 < len(text) < 1000 and text not in unique_items:
                unique_items.add(text)
                content.append(f"- {text}")
        return '\n'.join(content) if len(content) > 1 else ''

    async def scrape(self) -> str:
        unique_items = set()
        output = ['🔸 JOB OPENINGS:']
        for job, status in self.job_openings.items():
            if status == 1:
                output.append(f"- {job}")
                unique_items.add(job)

        async with async_playwright() as p:
            browser = await p.chromium.launch(headless=True)
            context = await browser.new_context(user_agent=random.choice(self.user_agents))
            page = await context.new_page()

            for url in self.urls:
                try:
                    logger.info(f"Scraping {url}")
                    await page.goto(url, wait_until='domcontentloaded', timeout=60000)
                    await page.wait_for_load_state('networkidle', timeout=20000)
                    html = await page.content()
                    section_name = self.get_section_name(url, html)
                    section_content = await self.extract_section(html, section_name, unique_items)
                    if section_content:
                        output.append(section_content)
                except PlaywrightTimeoutError:
                    logger.warning(f"Timeout scraping {url}")
                except Exception as e:
                    logger.error(f"Error scraping {url}: {e}")

            await browser.close()

        new_content = '\n'.join(output)
        if self.compute_md5(new_content) != self.compute_md5(self.latest_content):
            self.cache_file.parent.mkdir(parents=True, exist_ok=True)
            self.cache_file.write_text(new_content, encoding='utf-8')  # <-- FIXED ENCODING
            logger.info("Updated scraped content cache.")
        else:
            logger.info("No changes detected in content.")

        return new_content

# Entry Point
if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python scraper.py <config.json path>")
        exit(1)
    config_path = sys.argv[1]
    asyncio.run(WebScraper(config_path).scrape())
