// Базовый URL API
const API_BASE = '';

// Общая функция для запросов
async function apiRequest(url, method = 'GET', data = null) {
    const options = {
        method,
        headers: {
            'Content-Type': 'application/json',
        },
    };

    if (data) {
        options.body = JSON.stringify(data);
    }

    try {
        const response = await fetch(url, options);

        if (response.status === 403) {
            window.location.href = 'login.html';
            throw new Error('Доступ запрещен');
        }

        if (!response.ok) {
            if (response.status === 401) {
                window.location.href = 'login.html';
                throw new Error('Не авторизован');
            }
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        if (method === 'DELETE') {
            return null;
        }

        return await response.json();
    } catch (error) {
        console.error('API Request Error:', error);
        throw error;
    }
}

// Форматирование даты
function formatDate(dateString) {
    if (!dateString) return '-';
    const date = new Date(dateString);
    return date.toLocaleDateString('ru-RU');
}

// Отображение уведомлений
function showNotification(message, type = 'info', containerId = 'notifications') {
    const container = document.getElementById(containerId);
    if (!container) return;

    const notification = document.createElement('div');
    notification.className = `notification ${type}`;
    notification.textContent = message;

    container.appendChild(notification);

    setTimeout(() => {
        notification.remove();
    }, 3000);
}

// Загрузка списка для select
async function loadSelectOptions(selectId, apiUrl, valueField, textField, defaultText = 'Выберите...') {
    const select = document.getElementById(selectId);
    if (!select) return;

    try {
        const items = await apiRequest(apiUrl);
        select.innerHTML = `<option value="">${defaultText}</option>`;
        items.forEach(item => {
            const option = document.createElement('option');
            // Исправлено: используем правильные названия полей
            option.value = item[valueField];
            option.textContent = item[textField];
            select.appendChild(option);
        });
    } catch (error) {
        console.error('Ошибка загрузки данных для select:', error);
    }
}

// Выход из системы
async function logout() {
    try {
        await fetch('/api/logout', { method: 'POST' });
        window.location.href = 'login.html';
    } catch (error) {
        console.error('Ошибка при выходе:', error);
    }
}